using System;
using System.Net;
using Microsoft.Extensions.Options;

namespace Percolator.Application.ReverseSignal;

public sealed class CallbackEndpointValidator : ICallbackEndpointValidator
{
    private readonly IOptions<ReverseSignalOptions> _options;

    public CallbackEndpointValidator(IOptions<ReverseSignalOptions> options)
    {
        _options = options;
    }

    public CallbackEndpointValidationResult Validate(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new CallbackEndpointValidationResult(false, "Host is required.", false, false);
        }

        if (port is < 1 or > 65535)
        {
            return new CallbackEndpointValidationResult(false, "Port is out of range.", false, false);
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            var isLanTarget = IsLanTarget(ip);
            if (isLanTarget && !_options.Value.AllowLan)
            {
                return new CallbackEndpointValidationResult(false, "LAN targets are not allowed.", true, true);
            }

            return new CallbackEndpointValidationResult(true, null, true, isLanTarget);
        }

        var hostType = Uri.CheckHostName(host);
        if (hostType is UriHostNameType.Dns)
        {
            return new CallbackEndpointValidationResult(true, null, false, false);
        }

        return new CallbackEndpointValidationResult(false, "Host is not a valid DNS name or IP address.", false, false);
    }

    private static bool IsLanTarget(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;

            var bytes = ip.GetAddressBytes();

            // fc00::/7 (ULA)
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            // fe80::/10 (link-local)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;

            return false;
        }

        return false;
    }
}
