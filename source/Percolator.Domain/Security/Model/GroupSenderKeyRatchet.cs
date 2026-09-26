using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed class GroupSenderKeyRatchet : AggregateRoot<Guid>, ISensitiveSecret
{
    public override Guid Id { get; }
    public ConversationId ConversationId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }

    private byte[]? _chainKeyBytes;
    public uint Iteration { get; private set; }
    public bool IsZeroized { get; private set; }

    public GroupSenderKeyRatchet(
        ConversationId conversationId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey initialChainKey,
        uint initialIteration = 0,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        ConversationId = conversationId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        _chainKeyBytes = initialChainKey.Span.ToArray();
        Iteration = initialIteration;
    }

    public DomainResult<(uint Iteration, MessageKey Key)> Advance(ICryptoEngine engine)
    {
        if (IsZeroized || _chainKeyBytes == null)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("INVALID_RATCHET_STATE", "Sender key ratchet has been zeroized."));
        }

        var currentChainKey = ChainKey.FromSpan(_chainKeyBytes);
        var (nextChainKey, messageKey) = engine.StepRatchet(currentChainKey);

        CryptographicOperations.ZeroMemory(_chainKeyBytes);
        _chainKeyBytes = nextChainKey.Span.ToArray();

        uint currentIteration = Iteration++;
        return DomainResult<(uint, MessageKey)>.Success((currentIteration, messageKey));
    }

    public void Zeroize()
    {
        if (_chainKeyBytes != null)
        {
            CryptographicOperations.ZeroMemory(_chainKeyBytes);
            _chainKeyBytes = null;
        }

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
