namespace Percolator.Application.Invitations;

using System;
using System.Buffers.Binary;
using System.Text;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

public sealed class PeerInvitationCodec : IPeerInvitationCodec
{
    private const string SchemePrefix = "percolator://invite/";
    private const byte BinaryVersion = 1;

    public string Encode(PeerInvitationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.IdentityId.IsValid) throw new ArgumentException("IdentityId is invalid.", nameof(payload));
        if (payload.IdentityKey == null || payload.IdentityKey.Span.Length != 32) throw new ArgumentException("IdentityKey must be 32 bytes.", nameof(payload));
        if (payload.RelayEndpoint == null || string.IsNullOrWhiteSpace(payload.RelayEndpoint.Value)) throw new ArgumentException("RelayEndpoint is required.", nameof(payload));
        if (!payload.RoutingToken.IsValid) throw new ArgumentException("RoutingToken is invalid.", nameof(payload));

        var endpointBytes = Encoding.UTF8.GetBytes(payload.RelayEndpoint.Value);
        if (endpointBytes.Length > ushort.MaxValue) throw new ArgumentException("RelayEndpoint is too long.", nameof(payload));

        var totalLen = 1 + 16 + 32 + 16 + 2 + endpointBytes.Length;
        var buffer = new byte[totalLen];

        buffer[0] = BinaryVersion;
        payload.IdentityId.ToByteArray().CopyTo(buffer.AsSpan(1, 16));
        payload.IdentityKey.Span.CopyTo(buffer.AsSpan(17, 32));
        payload.RoutingToken.ToByteArray().CopyTo(buffer.AsSpan(49, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(65, 2), (ushort)endpointBytes.Length);
        endpointBytes.CopyTo(buffer.AsSpan(67, endpointBytes.Length));

        return $"{SchemePrefix}{EncodeBase64Url(buffer)}";
    }

    public DomainResult<PeerInvitationPayload> Decode(string invitationString)
    {
        if (string.IsNullOrWhiteSpace(invitationString))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION", "Invitation string cannot be empty."));
        }

        var input = invitationString.Trim();
        if (!input.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION_URI", $"Invitation URI must begin with '{SchemePrefix}'."));
        }

        var rawBase64Url = input.Substring(SchemePrefix.Length);
        if (string.IsNullOrWhiteSpace(rawBase64Url))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION_PAYLOAD", "Invitation contains no payload."));
        }

        var bytes = DecodeBase64Url(rawBase64Url);
        if (bytes == null || bytes.Length < 67)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION_PAYLOAD", "Binary invitation payload is malformed or too short."));
        }

        if (bytes[0] != BinaryVersion)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("UNSUPPORTED_VERSION", $"Unsupported invitation format byte: 0x{bytes[0]:X2}."));
        }

        var idBytes = bytes.AsSpan(1, 16);
        if (IsAllZero(idBytes))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_IDENTITY_ID", "IdentityId cannot be all zeroes."));
        }
        var identityId = PublicIdentityId.FromBytes(idBytes);
        if (!identityId.IsValid)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_IDENTITY_ID", "IdentityId is invalid."));
        }

        var ikBytes = bytes.AsSpan(17, 32);
        if (IsAllZero(ikBytes))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_IDENTITY_KEY", "IdentityKey cannot be all zeroes."));
        }
        var identityKey = IdentityKey.FromSpan(ikBytes);

        var routeBytes = bytes.AsSpan(49, 16);
        if (IsAllZero(routeBytes))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_ROUTING_TOKEN", "RoutingToken cannot be all zeroes."));
        }
        var routingToken = BlindedRoutingToken.FromBytes(routeBytes);
        if (!routingToken.IsValid)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_ROUTING_TOKEN", "RoutingToken is invalid."));
        }

        var endpointLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(65, 2));
        if (endpointLen == 0 || bytes.Length != 67 + endpointLen)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION_PAYLOAD", "RelayEndpoint length is invalid or does not match payload size."));
        }

        var endpointString = Encoding.UTF8.GetString(bytes.AsSpan(67, endpointLen));
        var relayResult = RelayEndpoint.Create(endpointString);
        if (relayResult.IsFailure)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_RELAY_ENDPOINT", $"Relay endpoint in invitation is invalid: {relayResult.Error.Description}"));
        }

        return DomainResult<PeerInvitationPayload>.Success(
            new PeerInvitationPayload(identityId, identityKey, relayResult.Value, routingToken));
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[]? DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != 0) return false;
        }
        return true;
    }
}
