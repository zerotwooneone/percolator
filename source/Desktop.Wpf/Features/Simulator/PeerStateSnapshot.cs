using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Simulator.Models;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed record PeerStateSnapshot(
   Percolator.Network.PeerId PeerId,
    int SelfIdentityId,
    string? DisplayName,
    bool IsOnline,
    bool IsRelayCapable,
    byte[] IdentitySigningKeySpki,
    byte[] IdentitySigningKeyPrivateKeyEcPrivateKey,
    ConnectionMode ConnectionMode,
    string? Host,
    int Port,
   Percolator.Network.PeerId RelayPeerId,
    SimulatorPeerUiState UiState,
    Guid? InboundReverseSignalPendingCorrelationId,
    byte[]? TargetPublicKeyHash,
    ConnectionMode? SelectedRouteMode,
    string? DirectEndpoint,
   Percolator.Network.PeerId? RelayHostPeerId,
    string? Phase,
    DateTimeOffset? NotUntilUtc,
    string? LastError,
    IReadOnlyList<SimulatorHandshakeAttemptState> HandshakeAttempts,
    IdentityPublicKeyHash? PendingStandardHandshakeToMainResponderPublicKeyHash,
    Guid? PendingStandardHandshakeToMainTemporarySessionId,
    IReadOnlyList<Guid> KnownPeerIds,
    IReadOnlyList<PublishedPreKeyBundleSnapshot> PublishedPreKeyBundles,
    IReadOnlyList<SessionSnapshot> Sessions,
    IReadOnlyList<SignedPreKeySnapshot> SignedPreKeys,
    IReadOnlyList<OutboundInviteSnapshot> OutboundInvites,
    IReadOnlyList<PendingInviteHandshakeResponseSnapshot> PendingInviteHandshakeResponses,
    IReadOnlyList<SimulatedChatMessageSnapshot> RecentChatMessages,
    IReadOnlyList<SimulatedOneTimePreKeyPrivateSnapshot> OneTimePreKeysPrivate);

public sealed record PublishedPreKeyBundleSnapshot(
    IdentityPublicKeyHash RecipientPublicKeyHash,
   Percolator.Network.PeerId LogicalOwnerPeerId,
    byte[] IdentityKey,
    Guid SignedPreKeyId,
    byte[] SignedPreKey,
    byte[] PreKeySignature,
    IReadOnlyList<OneTimeKeySnapshot> OneTimeKeys,
    DateTimeOffset ExpiresUtc);

public sealed record OneTimeKeySnapshot(
    Guid Id,
    byte[] KeyBytes);

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

public sealed record SimulatedOneTimePreKeyPrivateSnapshot(
    Guid Id,
    byte[] PrivateKeyBytes,
    DateTimeOffset CreatedAtUtc);
