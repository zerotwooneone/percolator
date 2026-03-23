namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorStateDto
{
    public int Version { get; set; } = 1;
    public List<SimulatedPeerDto> Peers { get; set; } = new();
    public List<GroupConversationDto> Groups { get; set; } = new();
}

public sealed class GroupConversationDto
{
    public int Version { get; set; } = 1;
    public Guid GroupId { get; set; }
    public string? Name { get; set; }
    public List<Guid> ParticipantPeerIds { get; set; } = new();
}

public sealed class SimulatedPeerDto
{
    public Guid PeerId { get; set; }
    public int SelfIdentityId { get; set; }
    public string? DisplayName { get; set; }
    public bool IsOnline { get; set; } = true;
    public byte[] IdentityPublicKeyHash { get; set; } = Array.Empty<byte>();
    public SimulatedPeerConnectionDto Connection { get; set; } = new();
    public List<Guid> KnownPeerIds { get; set; } = new();
    public List<Guid> PublishedKeysToPeerIds { get; set; } = new();
    public SimulatedPeerPreKeyStateDto PreKeys { get; set; } = new();
    public SimulatedPeerRuntimeStoreDto RuntimeStore { get; set; } = new();

    public SimulatorPeerUiState UiState { get; set; } = SimulatorPeerUiState.Ready;
    public Guid? PendingCorrelationId { get; set; }
    public byte[]? TargetPublicKeyHash { get; set; }
    public ConnectionMode? SelectedRouteMode { get; set; }
    public string? DirectEndpoint { get; set; }
    public Guid? RelayHostPeerId { get; set; }
    public string? Phase { get; set; }
    public DateTimeOffset? NotUntilUtc { get; set; }
    public string? LastError { get; set; }
    public List<SimulatorHandshakeAttemptState> HandshakeAttempts { get; set; } = new();

    public byte[]? PendingStandardHandshakeToMainResponderPublicKeyHash { get; set; }
    public Guid? PendingStandardHandshakeToMainTemporarySessionId { get; set; }

    public SimulatedPeerRelayStateDto Relay { get; set; } = new();
    public SimulatedPeerReverseSignalKeysDto ReverseSignalKeys { get; set; } = new();
}

public sealed class SimulatedPeerRuntimeStoreDto
{
    public int Version { get; set; } = 1;
    public List<SimulatedSecureSessionDto> Sessions { get; set; } = new();
    public List<SimulatedSignedPreKeyDto> SignedPreKeys { get; set; } = new();
    public List<SimulatedOutboundInviteDto> OutboundInvites { get; set; } = new();
    public List<SimulatedPendingInviteHandshakeResponseDto> PendingInviteHandshakeResponses { get; set; } = new();
}

public sealed class SimulatedOutboundInviteDto
{
    public Guid CorrelationId { get; set; }
    public byte[] SignedPreKeyPrivateEcPrivateKey { get; set; } = Array.Empty<byte>();
}

public sealed class SimulatedPendingInviteHandshakeResponseDto
{
    public Guid CorrelationId { get; set; }
    public byte[] ResponseBytes { get; set; } = Array.Empty<byte>();
}

public sealed class SimulatedSecureSessionDto
{
    public Guid SessionId { get; set; }
    public Guid RemotePeerId { get; set; }
    public int ProtocolVersion { get; set; } = 1;

    public byte[] RootKey { get; set; } = Array.Empty<byte>();
    public byte[]? SendChainKey { get; set; }
    public ulong SendCounter { get; set; }
    public byte[]? RecvChainKey { get; set; }
    public ulong RecvCounter { get; set; }
    public ulong PrevChainLength { get; set; }
    public byte[]? RemoteRatchetKey { get; set; }
    public byte[]? DhRatchetPrivateKey { get; set; }

    public int SkippedKeysCount { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; }
}

public sealed class SimulatedSignedPreKeyDto
{
    public Guid SignedPreKeyId { get; set; }
    public byte[] PrivateEcPrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] PublicSpki { get; set; } = Array.Empty<byte>();
}

public sealed class SimulatedPeerReverseSignalKeysDto
{
    public int Version { get; set; } = 1;

    // Back-compat only (was introduced briefly). Prefer IdentitySigningKeyPrivateKeyEcPrivateKey.
    public byte[] IdentitySigningKeyPrivateKeyPkcs8 { get; set; } = Array.Empty<byte>();
    public byte[] IdentitySigningKeyPrivateKeyEcPrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] IdentitySigningKeySpki { get; set; } = Array.Empty<byte>();
}

public enum ConnectionMode
{
    Direct = 0,
    ViaRelay = 1
}

public sealed class SimulatedPeerConnectionDto
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.Direct;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 0;
    public Guid RelayPeerId { get; set; }
}

public sealed class SimulatedPeerRelayStateDto
{
    public bool IsRelayCapable { get; set; }
    public List<Guid> ActiveSessionsPeerIds { get; set; } = new();
    public SimulatedRelayOpaqueQueueDto OpaqueQueue { get; set; } = new();
    public SimulatedRelayPreKeyStoreDto PreKeyStore { get; set; } = new();
}

public sealed class SimulatedRelayOpaqueQueueDto
{
    public int Version { get; set; } = 1;
    public List<RelayQueuedBlobDto> Items { get; set; } = new();
}

public sealed class RelayQueuedBlobDto
{
    public Guid AckId { get; set; }
    public byte[] RecipientRoutingKey { get; set; } = Array.Empty<byte>();
    public byte[] OpaqueBytes { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedUtc { get; set; }
    public string? DebugType { get; set; }
}

public sealed class SimulatedRelayPreKeyStoreDto
{
    public int Version { get; set; } = 1;
    public List<PublishedPreKeyBundleDto> PublishedBundles { get; set; } = new();
}

public sealed class PublishedPreKeyBundleDto
{
    public byte[] RecipientPublicKeyHash { get; set; } = Array.Empty<byte>();
    public Guid LogicalOwnerPeerId { get; set; }
    public byte[] BundleBytes { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ExpiresUtc { get; set; }
}

public sealed class SimulatedPeerPreKeyStateDto
{
    public byte[]? IdentitySigningKeySpki { get; set; }
    public byte[]? SignedPreKeySpki { get; set; }
    public byte[]? SignedPreKeySignature { get; set; }
    public Guid? SignedPreKeyId { get; set; }
    public List<SimulatedOneTimePreKeyDto> OneTimePreKeys { get; set; } = new();
    public DateTimeOffset? ExpiresUtc { get; set; }
}

public sealed class SimulatedOneTimePreKeyDto
{
    public Guid Id { get; set; }
    public byte[] PublicKeySpki { get; set; } = Array.Empty<byte>();
}
