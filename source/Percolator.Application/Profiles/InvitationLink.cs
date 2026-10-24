using System.Text;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Profiles;

public enum InvitationKind
{
    Direct = 1,
    Relayed = 2
}

/// <summary>
/// Representation for sharing contact invitations and bootstrap endpoints.
/// Direct format: percolator://{Host}[:{Port}]/{PublicKey}
/// Relayed format: Base64 payload or percolator://invite/{Base64}
/// </summary>
public sealed record InvitationLink
{
    public const string Scheme = "percolator";
    public const byte RelayedPayloadVersion = 0x02;

    public InvitationKind Kind { get; }

    // Direct P2P properties
    public string Host { get; }
    public int? Port { get; }
    public string PublicKey { get; }

    // Relayed invitation properties
    public PublicIdentityId? PeerIdentityId { get; }
    public DeviceId? PeerDeviceId { get; }
    public IdentityKey? IdentityKey { get; }
    public RelayEndpoint? RelayEndpoint { get; }
    public BlindedRoutingToken? RoutingToken { get; }
    public DeliveryToken? DeliveryToken { get; }

    public InvitationLink(string host, int? port, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535 if specified.");
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Public key cannot be empty.", nameof(publicKey));
        }

        Kind = InvitationKind.Direct;
        Host = host;
        Port = port;
        PublicKey = publicKey.Trim();
    }

    public InvitationLink(string host, string publicKey)
        : this(host, null, publicKey)
    {
    }

    private InvitationLink(
        PublicIdentityId peerIdentityId,
        DeviceId peerDeviceId,
        IdentityKey identityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken)
    {
        Kind = InvitationKind.Relayed;
        PeerIdentityId = peerIdentityId;
        PeerDeviceId = peerDeviceId;
        IdentityKey = identityKey;
        RelayEndpoint = relayEndpoint;
        RoutingToken = routingToken;
        DeliveryToken = deliveryToken;

        Host = relayEndpoint.Value;
        Port = relayEndpoint.Port;
        PublicKey = Convert.ToHexString(identityKey.Span);
    }

    public static InvitationLink CreateRelayed(
        PublicIdentityId peerIdentityId,
        DeviceId peerDeviceId,
        IdentityKey identityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken)
    {
        if (!peerIdentityId.IsValid) throw new ArgumentException("Invalid peer identity ID.", nameof(peerIdentityId));
        if (!peerDeviceId.IsValid) throw new ArgumentException("Invalid peer device ID.", nameof(peerDeviceId));
        ArgumentNullException.ThrowIfNull(identityKey);
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        if (!routingToken.IsValid) throw new ArgumentException("Invalid routing token.", nameof(routingToken));
        ArgumentNullException.ThrowIfNull(deliveryToken);

        return new InvitationLink(
            peerIdentityId,
            peerDeviceId,
            identityKey,
            relayEndpoint,
            routingToken,
            deliveryToken);
    }

    public string ToBase64()
    {
        if (Kind != InvitationKind.Relayed)
        {
            throw new InvalidOperationException("Only relayed invitations can be serialized to Base64.");
        }

        var endpointBytes = Encoding.UTF8.GetBytes(RelayEndpoint!.Value);
        var totalLen = 1 + 16 + 4 + 32 + 16 + 32 + 2 + endpointBytes.Length;
        var buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        span[0] = RelayedPayloadVersion;
        PeerIdentityId!.Value.TryWriteBytes(span.Slice(1, 16));
        BitConverter.TryWriteBytes(span.Slice(17, 4), PeerDeviceId!.Value.Value);
        IdentityKey!.Span.CopyTo(span.Slice(21, 32));
        RoutingToken!.Value.TryWriteBytes(span.Slice(53, 16));
        DeliveryToken!.Span.CopyTo(span.Slice(69, 32));
        BitConverter.TryWriteBytes(span.Slice(101, 2), (ushort)endpointBytes.Length);
        endpointBytes.CopyTo(span.Slice(103));

        return Convert.ToBase64String(buffer);
    }

    public override string ToString()
    {
        if (Kind == InvitationKind.Relayed)
        {
            return $"{Scheme}://invite/{ToBase64()}";
        }

        return Port.HasValue
            ? $"{Scheme}://{Host}:{Port.Value}/{PublicKey}"
            : $"{Scheme}://{Host}/{PublicKey}";
    }

    public static InvitationLink Parse(string link)
    {
        if (!TryParse(link, out var result, out var error))
        {
            throw new FormatException(error);
        }

        return result!;
    }

    public static bool TryParse(string? link, out InvitationLink? result, out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(link))
        {
            errorMessage = "Invitation link is empty.";
            return false;
        }

        link = link.Trim();

        const string invitePrefix = Scheme + "://invite/";
        if (link.StartsWith(invitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var b64 = link.Substring(invitePrefix.Length).Trim('/');
            return TryParseBase64(b64, out result, out errorMessage);
        }

        if (!link.Contains("://", StringComparison.Ordinal))
        {
            if (TryParseBase64(link, out result, out _))
            {
                return true;
            }
        }

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            errorMessage = "Invitation link is not a valid URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Invalid scheme '{uri.Scheme}'. Expected '{Scheme}'.";
            return false;
        }

        if (string.Equals(uri.Host, "invite", StringComparison.OrdinalIgnoreCase))
        {
            var b64 = uri.AbsolutePath.Trim('/');
            return TryParseBase64(b64, out result, out errorMessage);
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            errorMessage = "Host is missing from the invitation link.";
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
        {
            errorMessage = "Public key is missing from the invitation link.";
            return false;
        }

        int? port = uri.Port is > 0 and <= 65535 ? uri.Port : null;

        result = new InvitationLink(uri.Host, port, path);
        return true;
    }

    private static bool TryParseBase64(string base64, out InvitationLink? result, out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            errorMessage = "Invalid Base64 payload in invitation link.";
            return false;
        }

        if (bytes.Length < 103 || bytes[0] != RelayedPayloadVersion)
        {
            errorMessage = "Unsupported or invalid invitation binary payload.";
            return false;
        }

        var span = bytes.AsSpan();
        var peerId = PublicIdentityId.FromBytes(span.Slice(1, 16));
        var deviceId = new DeviceId(BitConverter.ToUInt32(span.Slice(17, 4)));
        var ik = IdentityKey.FromSpan(span.Slice(21, 32));
        var routingToken = BlindedRoutingToken.FromBytes(span.Slice(53, 16));
        var deliveryToken = DeliveryToken.FromSpan(span.Slice(69, 32));
        var endpointLen = BitConverter.ToUInt16(span.Slice(101, 2));

        if (103 + endpointLen > bytes.Length)
        {
            errorMessage = "Malformed invitation payload: endpoint length overflow.";
            return false;
        }

        var endpointStr = Encoding.UTF8.GetString(span.Slice(103, endpointLen));
        var endpointResult = RelayEndpoint.Create(endpointStr);
        if (endpointResult.IsFailure)
        {
            errorMessage = $"Malformed invitation payload: {endpointResult.Error.Description}";
            return false;
        }

        result = new InvitationLink(peerId, deviceId, ik, endpointResult.Value!, routingToken, deliveryToken);
        return true;
    }
}
