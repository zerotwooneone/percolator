using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// Strongly typed value object representing the network address of a Percolator Relay node.
/// Enforces domain validation, supports host/port parsing and URI interoperability,
/// and eliminates primitive obsession (no implicit/explicit cast operators).
/// </summary>
public sealed record RelayEndpoint
{
    public string Value { get; }
    public string Host { get; }
    public int? Port { get; }

    private RelayEndpoint(string value, string host, int? port)
    {
        Value = value;
        Host = host;
        Port = port;
    }

    public static DomainResult<RelayEndpoint> Create(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DomainResult<RelayEndpoint>.Failure(new DomainError(
                "INVALID_RELAY_ENDPOINT", "Relay endpoint cannot be null, empty, or whitespace."));
        }

        var trimmed = endpoint.Trim();

        if (trimmed.Contains("://") && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var port = uri.Port > 0 ? (int?)uri.Port : null;
            return DomainResult<RelayEndpoint>.Success(new RelayEndpoint(trimmed, host, port));
        }

        if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is var closingBracket && closingBracket > 0)
        {
            var hostPart = trimmed.Substring(1, closingBracket - 1);
            if (closingBracket + 1 < trimmed.Length && trimmed[closingBracket + 1] == ':')
            {
                var portPart = trimmed.Substring(closingBracket + 2);
                if (int.TryParse(portPart, out var port) && port is >= 1 and <= 65535)
                {
                    return DomainResult<RelayEndpoint>.Success(new RelayEndpoint(trimmed, hostPart, port));
                }

                return DomainResult<RelayEndpoint>.Failure(new DomainError(
                    "INVALID_RELAY_PORT", $"Port '{portPart}' in relay endpoint must be an integer between 1 and 65535."));
            }

            return DomainResult<RelayEndpoint>.Success(new RelayEndpoint(trimmed, hostPart, null));
        }

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex > 0 && colonIndex < trimmed.Length - 1)
        {
            if (trimmed.IndexOf(':') == colonIndex)
            {
                var hostPart = trimmed.Substring(0, colonIndex);
                var portPart = trimmed.Substring(colonIndex + 1);

                if (int.TryParse(portPart, out var port) && port is >= 1 and <= 65535)
                {
                    return DomainResult<RelayEndpoint>.Success(new RelayEndpoint(trimmed, hostPart, port));
                }

                return DomainResult<RelayEndpoint>.Failure(new DomainError(
                    "INVALID_RELAY_PORT", $"Port '{portPart}' in relay endpoint must be an integer between 1 and 65535."));
            }
        }

        return DomainResult<RelayEndpoint>.Success(new RelayEndpoint(trimmed, trimmed, null));
    }

    public static RelayEndpoint Parse(string endpoint)
    {
        var result = Create(endpoint);
        if (result.IsFailure)
        {
            throw new ArgumentException(result.Error.Description, nameof(endpoint));
        }

        return result.Value!;
    }

    public static RelayEndpoint FromHostAndPort(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        var trimmedHost = host.Trim();
        return new RelayEndpoint($"{trimmedHost}:{port}", trimmedHost, port);
    }

    public static RelayEndpoint FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.Host;
        var port = uri.Port > 0 ? (int?)uri.Port : null;
        return new RelayEndpoint(uri.ToString(), host, port);
    }

    public override string ToString() => Value;
}
