namespace Percolator.Infrastructure.Transport;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;

public sealed class CallbackEndpointValidatorOptions
{
    public bool AllowLoopback { get; set; } = false;
    public bool AllowLan { get; set; } = false;
}

public sealed class CallbackEndpointValidator
{
    private readonly CallbackEndpointValidatorOptions _options;

    public CallbackEndpointValidator(CallbackEndpointValidatorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DomainResult<IPEndPoint>> ValidateEndpointAsync(
        string host,
        int port,
        CallbackEndpointValidatorOptions? overrideOptions = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DomainResult<IPEndPoint>.Failure(new DomainError("INVALID_HOST", "Host cannot be empty."));
        }

        if (port <= 0 || port > 65535)
        {
            return DomainResult<IPEndPoint>.Failure(new DomainError("INVALID_PORT", $"Port {port} is out of valid range (1-65535)."));
        }

        var opts = overrideOptions ?? _options;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            addresses = [parsedIp];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (Exception ex)
            {
                return DomainResult<IPEndPoint>.Failure(new DomainError("DNS_RESOLUTION_FAILED", ex.Message));
            }
        }

        if (addresses.Length == 0)
        {
            return DomainResult<IPEndPoint>.Failure(new DomainError("DNS_NO_ADDRESSES", $"No IP addresses found for host '{host}'."));
        }

        foreach (var ip in addresses)
        {
            var checkResult = CheckAddress(ip, opts);
            if (checkResult.IsFailure)
            {
                return DomainResult<IPEndPoint>.Failure(checkResult.Error!);
            }
        }

        return DomainResult<IPEndPoint>.Success(new IPEndPoint(addresses[0], port));
    }

    public DomainResult CheckAddress(IPAddress address, CallbackEndpointValidatorOptions opts)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return DomainResult.Failure(new DomainError("UNSPECIFIED_ADDRESS", "Unspecified wildcard addresses are not permitted."));
        }

        if (IPAddress.IsLoopback(address))
        {
            if (!opts.AllowLoopback)
            {
                return DomainResult.Failure(new DomainError("LOOPBACK_BLOCKED", "Loopback addresses are blocked."));
            }
            return DomainResult.Success();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return DomainResult.Failure(new DomainError("LINK_LOCAL_BLOCKED", "Link-local addresses are blocked."));
            }

            bool isPrivate = bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);

            if (isPrivate && !opts.AllowLan)
            {
                return DomainResult.Failure(new DomainError("PRIVATE_NETWORK_BLOCKED", "Private network/RFC1918 addresses are blocked."));
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();

            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            {
                return DomainResult.Failure(new DomainError("LINK_LOCAL_BLOCKED", "IPv6 link-local addresses are blocked."));
            }

            if ((bytes[0] & 0xFE) == 0xFC)
            {
                if (!opts.AllowLan)
                {
                    return DomainResult.Failure(new DomainError("PRIVATE_NETWORK_BLOCKED", "IPv6 Unique Local Addresses (ULA) are blocked."));
                }
            }
        }

        return DomainResult.Success();
    }
}
