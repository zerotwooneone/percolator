using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Model;

public sealed class GroupSenderKeyRatchet : AggregateRoot<SessionId>, ISensitiveSecret
{
    public override SessionId Id { get; }
    public ConversationId ConversationId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }

    private ChainKey? _chainKey;
    public uint Iteration { get; private set; }
    public bool IsZeroized { get; private set; }

    public GroupSenderKeyRatchet(
        ConversationId conversationId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ChainKey initialChainKey,
        uint initialIteration = 0,
        SessionId? id = null)
    {
        Id = id ?? SessionId.New();
        ConversationId = conversationId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        _chainKey = ChainKey.FromSpan(initialChainKey.Span);
        Iteration = initialIteration;
    }

    public DomainResult<(uint Iteration, MessageKey Key)> Advance(ICryptoEngine engine)
    {
        if (IsZeroized || _chainKey == null)
        {
            return DomainResult<(uint, MessageKey)>.Failure(new DomainError("INVALID_RATCHET_STATE", "Sender key ratchet has been zeroized."));
        }

        var previousKey = _chainKey;
        var (nextChainKey, messageKey) = engine.StepRatchet(previousKey);

        _chainKey = nextChainKey;
        previousKey.Dispose();

        uint currentIteration = Iteration++;
        return DomainResult<(uint, MessageKey)>.Success((currentIteration, messageKey));
    }

    public void Zeroize()
    {
        if (_chainKey != null)
        {
            _chainKey.Dispose();
            _chainKey = null;
        }

        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
