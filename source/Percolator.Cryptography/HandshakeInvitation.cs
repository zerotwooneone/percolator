using System;
using Google.Protobuf;
using Percolator.Contracts;
using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record HandshakeInvitation(byte[] Value) : ByteArrayRecord(Value);

public sealed record ParsedInvitation(
    RatchetIdentityKey InitiatorIdentityKey,
    RatchetEphemeralKey InitiatorEphemeralKey,
    string SignedPreKeyId,
    string? OneTimePreKeyId);

public interface IHandshakeInvitationParser
{
    ParsedInvitation Parse(HandshakeInvitation invitation);
}

public sealed class HandshakeInvitationParser : IHandshakeInvitationParser
{
    public ParsedInvitation Parse(HandshakeInvitation invitation) => ParseCore(invitation);

    internal static ParsedInvitation Internal_Parse(HandshakeInvitation invitation) => ParseCore(invitation);

    private static ParsedInvitation ParseCore(HandshakeInvitation invitation)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));

        EstablishSessionRequest request;
        try
        {
            request = EstablishSessionRequest.Parser.ParseFrom(invitation.Value);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOperationException("Invalid handshake invitation payload.", ex);
        }

        if (request.IdentitySigningKey.IsEmpty
            || request.EphemeralKey.IsEmpty
            || request.PrekeyId.IsEmpty)
        {
            throw new InvalidOperationException("Handshake invitation is missing required X3DH fields.");
        }

        var ik = new RatchetIdentityKey(request.IdentitySigningKey.ToByteArray());
        var ek = new RatchetEphemeralKey(request.EphemeralKey.ToByteArray());
        var spkId = request.PrekeyId.ToStringUtf8();
        string? opkId = request.OnetimePrekeyId.IsEmpty ? null : request.OnetimePrekeyId.ToStringUtf8();

        return new ParsedInvitation(ik, ek, spkId, opkId);
    }
}

public static class HandshakeInvitationBuilder
{
    public static HandshakeInvitation Build(
        RatchetIdentityKey initiatorIdentityKey,
        RatchetEphemeralKey initiatorEphemeralKey,
        string signedPreKeyId,
        string? oneTimePreKeyId = null)
    {
        if (initiatorIdentityKey is null) throw new ArgumentNullException(nameof(initiatorIdentityKey));
        if (initiatorEphemeralKey is null) throw new ArgumentNullException(nameof(initiatorEphemeralKey));
        if (string.IsNullOrWhiteSpace(signedPreKeyId)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(signedPreKeyId));

        var request = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(initiatorIdentityKey.Value),
            EphemeralKey = ByteString.CopyFrom(initiatorEphemeralKey.Value),
            PrekeyId = ByteString.CopyFromUtf8(signedPreKeyId)
        };

        if (!string.IsNullOrWhiteSpace(oneTimePreKeyId))
        {
            request.OnetimePrekeyId = ByteString.CopyFromUtf8(oneTimePreKeyId);
        }

        var bytes = request.ToByteArray();
        return new HandshakeInvitation(bytes);
    }
}
