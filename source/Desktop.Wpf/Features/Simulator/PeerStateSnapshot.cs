using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public sealed record PeerStateSnapshot(
    Guid PeerId,
    string? DisplayName,
    bool IsOnline,
    bool IsRelayCapable,
    byte[] IdentitySigningKeySpki,
    byte[] IdentitySigningKeyPrivateKeyEcPrivateKey,
    ConnectionMode ConnectionMode,
    string? Host,
    int Port,
    Guid RelayPeerId,
    SimulatorPeerUiState UiState,
    Guid? PendingCorrelationId,
    byte[]? TargetPublicKeyHash,
    ConnectionMode? SelectedRouteMode,
    string? DirectEndpoint,
    Guid? RelayHostPeerId,
    string? Phase,
    DateTimeOffset? NotUntilUtc,
    string? LastError,
    IReadOnlyList<Guid> PublishedKeysToPeerIds,
    IReadOnlyList<Guid> RelayActiveSessionsPeerIds,
    IReadOnlyList<SimulatorHandshakeAttemptState> HandshakeAttempts,
    byte[]? PendingStandardHandshakeToMainResponderPublicKeyHash,
    Guid? PendingStandardHandshakeToMainTemporarySessionId,
    IReadOnlyList<Guid> KnownPeerIds,
    IReadOnlyList<PublishedPreKeyBundleSnapshot> PublishedPreKeyBundles,
    IReadOnlyList<SessionSnapshot> Sessions,
    IReadOnlyList<SignedPreKeySnapshot> SignedPreKeys,
    IReadOnlyList<OutboundInviteSnapshot> OutboundInvites,
    IReadOnlyList<PendingInviteHandshakeResponseSnapshot> PendingInviteHandshakeResponses);

public sealed record PublishedPreKeyBundleSnapshot(
    byte[] RecipientPublicKeyHash,
    Guid LogicalOwnerPeerId,
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
