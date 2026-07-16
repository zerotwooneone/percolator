using Percolator.Chat.GroupMembership;
using Percolator.Identity;

namespace Percolator.Application.Chat;

/// <summary>
/// Represents either a PeerId (external node) or a ChatSelfId (local identity).
/// This discriminated union type guarantees one or the other, never both, never neither.
/// </summary>
public readonly record struct PeerOrSelfId
{
    private readonly PeerId? _peerId;
    private readonly ChatSelfId? _selfId;

    private PeerOrSelfId(PeerId peerId)
    {
        _peerId = peerId;
        _selfId = null;
    }

    private PeerOrSelfId(ChatSelfId selfId)
    {
        _peerId = null;
        _selfId = selfId;
    }

    public static PeerOrSelfId FromPeerId(PeerId peerId) => new PeerOrSelfId(peerId);
    public static PeerOrSelfId FromSelfId(ChatSelfId selfId) => new PeerOrSelfId(selfId);

    public bool IsPeer => _peerId.HasValue;
    public bool IsSelf => _selfId.HasValue;

    public PeerId PeerId => _peerId ?? throw new InvalidOperationException("Value is not a PeerId");
    public ChatSelfId SelfId => _selfId ?? throw new InvalidOperationException("Value is not a ChatSelfId");

    public T Match<T>(Func<PeerId, T> onPeer, Func<ChatSelfId, T> onSelf)
    {
        return IsPeer ? onPeer(PeerId) : onSelf(SelfId);
    }
}
