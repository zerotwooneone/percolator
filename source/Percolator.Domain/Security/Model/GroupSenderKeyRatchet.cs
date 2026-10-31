using System.ComponentModel;
using System.Security.Cryptography;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed record GroupSenderKeySnapshot(
    uint KeyId,
    uint Iteration,
    byte[] ChainKeyBytes,
    byte[]? SigningPrivateKeyBytes,
    IdentityKey? AuthorSigningPublicKey);

public sealed class GroupSenderKeyRatchet : AggregateRoot<SessionId>, ISensitiveSecret
{
    public override SessionId Id { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }
    public uint KeyId { get; }

    private ChainKey? _chainKey;
    private EphemeralPrivateKey? _signingPrivateKey;
    public IdentityKey? AuthorSigningPublicKey { get; }

    public uint Iteration { get; private set; }
    public bool IsZeroized { get; private set; }

    public GroupSenderKeyRatchet(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey initialChainKey,
        uint initialIteration = 0,
        uint keyId = 1,
        EphemeralPrivateKey? signingPrivateKey = null,
        IdentityKey? authorSigningPublicKey = null,
        SessionId? id = null)
    {
        Id = id ?? SessionId.New();
        ChannelId = channelId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        KeyId = keyId;
        _chainKey = ChainKey.FromSpan(initialChainKey.Span);
        _signingPrivateKey = signingPrivateKey != null ? EphemeralPrivateKey.FromSpan(signingPrivateKey.Span) : null;
        AuthorSigningPublicKey = authorSigningPublicKey;
        Iteration = initialIteration;
    }

    public bool TryExportChainKey(Span<byte> destination)
    {
        if (IsZeroized || _chainKey == null || destination.Length < 32)
        {
            return false;
        }

        _chainKey.Span.CopyTo(destination);
        return true;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public GroupSenderKeySnapshot ExportSnapshot()
    {
        return new GroupSenderKeySnapshot(
            KeyId,
            Iteration,
            _chainKey != null && !IsZeroized ? _chainKey.Span.ToArray() : [],
            _signingPrivateKey != null && !IsZeroized ? _signingPrivateKey.Span.ToArray() : null,
            AuthorSigningPublicKey);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GroupSenderKeyRatchet Rehydrate(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey chainKey,
        uint iteration,
        uint keyId,
        EphemeralPrivateKey? signingPrivateKey = null,
        IdentityKey? authorSigningPublicKey = null,
        SessionId? id = null)
    {
        if (!channelId.IsValid) throw new ArgumentException("ChannelId cannot be empty.", nameof(channelId));
        if (!authorId.IsValid) throw new ArgumentException("AuthorId cannot be empty.", nameof(authorId));
        if (!authorDeviceId.IsValid) throw new ArgumentException("AuthorDeviceId cannot be empty.", nameof(authorDeviceId));
        ArgumentNullException.ThrowIfNull(chainKey);
        if (keyId == 0) throw new ArgumentException("KeyId must be greater than zero.", nameof(keyId));

        return new GroupSenderKeyRatchet(
            channelId,
            authorId,
            authorDeviceId,
            chainKey,
            iteration,
            keyId,
            signingPrivateKey,
            authorSigningPublicKey,
            id);
    }

    public DomainResult<(uint Iteration, MessageKey Key)> Advance(ICryptoEngine engine)
    {
        if (IsZeroized || _chainKey == null)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("INVALID_RATCHET_STATE", "Sender key ratchet has been zeroized."));
        }

        var previousKey = _chainKey;
        var (nextChainKey, messageKey) = engine.StepRatchet(previousKey);

        previousKey.Dispose();
        _chainKey = nextChainKey;
        var emittedIteration = Iteration;
        Iteration++;

        return DomainResult<(uint, MessageKey)>.Success((emittedIteration, messageKey));
    }

    public DomainResult<byte[]> SignPayload(ReadOnlySpan<byte> message, ICryptoEngine engine)
    {
        if (IsZeroized || _signingPrivateKey == null)
        {
            return DomainResult<byte[]>.Failure(new DomainError("SIGNING_KEY_UNAVAILABLE", "Signing private key is missing or has been zeroized."));
        }

        var signature = engine.SignEd25519(_signingPrivateKey.Span, message);
        return DomainResult<byte[]>.Success(signature);
    }

    public void Zeroize()
    {
        if (IsZeroized)
        {
            return;
        }

        _chainKey?.Dispose();
        _chainKey = null;

        _signingPrivateKey?.Dispose();
        _signingPrivateKey = null;

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
