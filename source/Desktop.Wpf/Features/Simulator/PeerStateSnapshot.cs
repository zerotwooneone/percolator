using Desktop.Wpf.Features.Chat;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed record PeerStateSnapshot(
    PeerId PeerId,
    int SelfIdentityId,
    string? DisplayName,
    bool IsOnline,
    bool IsRelayCapable,
    byte[] IdentitySigningKeySpki,
    byte[] IdentitySigningKeyPrivateKeyEcPrivateKey,
    ConnectionMode ConnectionMode,
    string? Host,
    int Port,
    PeerId RelayPeerId,
    SimulatorPeerUiState UiState,
    Guid? InboundReverseSignalPendingCorrelationId,
    byte[]? TargetPublicKeyHash,
    ConnectionMode? SelectedRouteMode,
    string? DirectEndpoint,
    PeerId? RelayHostPeerId,
    string? Phase,
    DateTimeOffset? NotUntilUtc,
    string? LastError,
    IReadOnlyList<SimulatorHandshakeAttemptState> HandshakeAttempts,
    byte[]? PendingStandardHandshakeToMainResponderPublicKeyHash,
    Guid? PendingStandardHandshakeToMainTemporarySessionId,
    IReadOnlyList<Guid> KnownPeerIds,
    IReadOnlyList<PublishedPreKeyBundleSnapshot> PublishedPreKeyBundles,
    IReadOnlyList<SessionSnapshot> Sessions,
    IReadOnlyList<SignedPreKeySnapshot> SignedPreKeys,
    IReadOnlyList<OutboundInviteSnapshot> OutboundInvites,
    IReadOnlyList<PendingInviteHandshakeResponseSnapshot> PendingInviteHandshakeResponses,
    IReadOnlyList<SimulatedChatMessageSnapshot> RecentChatMessages);

public sealed record PublishedPreKeyBundleSnapshot(
    byte[] RecipientPublicKeyHash,
    PeerId LogicalOwnerPeerId,
    byte[] BundleBytes,
    DateTimeOffset ExpiresUtc);

public sealed record SessionSnapshot(
    Guid SessionId,
    Guid RemotePeerId,
    int ProtocolVersion,
    byte[] RootKey,
    byte[]? SendChainKey,
    ulong SendCounter,
    byte[]? RecvChainKey,
    ulong RecvCounter,
    ulong PrevChainLength,
    byte[]? RemoteRatchetKey,
    byte[]? DhRatchetPrivateKey,
    int SkippedKeysCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc);

public sealed record SignedPreKeySnapshot(Guid SignedPreKeyId, byte[] PrivateEcPrivateKey, byte[] PublicSpki);

public sealed record OutboundInviteSnapshot(Guid CorrelationId, byte[] SignedPreKeyPrivateEcPrivateKey);

public sealed record PendingInviteHandshakeResponseSnapshot(Guid CorrelationId, byte[] ResponseBytes);

public sealed record SimulatedChatMessageSnapshot(bool IsFromMain, string Content, DateTimeOffset ReceivedUtc);
