using System;
using System.Collections.Generic;

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
    public string? DisplayName { get; set; }
    public bool IsOnline { get; set; } = true;
    public SimulatedPeerConnectionDto Connection { get; set; } = new();
    public List<Guid> KnownPeerIds { get; set; } = new();
    public SimulatedPeerPreKeyStateDto PreKeys { get; set; } = new();
    public SimulatedPeerRelayStateDto Relay { get; set; } = new();
    public SimulatedPeerReverseSignalKeysDto ReverseSignalKeys { get; set; } = new();
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
    public byte[] RecipientRoutingKey { get; set; } = Array.Empty<byte>();
    public byte[] OpaqueBytes { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedUtc { get; set; }
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
