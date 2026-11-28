# Hybrid Relay-Only Transport + Pinned-Identity PreKey Invitation Plan

Target architecture: Unified Relay-Only Transport (self-hosted local Exchange or external Exchange) + Relay-Hosted PreKeys with Out-of-Band Pinned `IdentityKey` Invitations. Zero Direct P2P messaging/handshake paths. Zero JSON serialization.

---

## Step 1: Domain & PluginSdk Refactor

### 1.1 Deletions
- `Percolator.PluginSdk/IDirectEndpointRegistry.cs`
- `Percolator.PluginSdk/INetworkEndpointDiscoveryPort.cs` (retain `Percolator.Apps.FileTransfer/Ports/INetworkEndpointDiscoveryPort.cs` for P2P swarm chunking)

### 1.1b Retained Networking Primitives — `Percolator.Domain`
- Retain `PortRange` (`Percolator.Domain/Identities/ValueObjects/PortRange.cs` or relocate to `Percolator.Domain/Delivery/ValueObjects/PortRange.cs`): models canonical Percolator Exchange port range (5100–5199 per `DEC-CANONICAL-PORT-RANGE`) used when resolving/probing `percolator://<host>` Exchange endpoints without explicit ports, and used by `PortReservationService` / vault settings strictly for Self-Hosted Exchanges.

### 1.2 Modifications — `Percolator.PluginSdk`
- `Percolator.PluginSdk/DeliveryRoute.cs`: remove `DirectP2P = 1`; retain `RelayedOneToOne = 2`, `RelayedGroup = 3`.
- `Percolator.PluginSdk/BlobStorageMode.cs`: remove `DirectP2P = 1`; retain `RelayHosted = 2`, `P2PSwarm = 3`.
- `Percolator.PluginSdk/Profiles/IProfilePorts.cs`:
  - `ProfileSourceType`: remove `DirectP2P = 1`; retain `Relay = 2`.
  - `PeerMailboxRoutingCapability`: keep `RelayEndpoint` non-nullable `RelayEndpoint`.
- `Percolator.PluginSdk/Profiles/IInitialHandshakePayloadProvider.cs`: remove `bool allowDirectEndpoint` parameter from `CreateInitialPayloadAsync`.
- `Percolator.PluginSdk/IPeerConnectionService.cs`:
  - Delete `ConnectToDirectPeerAsync`.
  - Add `ConnectWithInvitationAsync` accepting Domain value object `PeerInvitation` (so PluginSdk has zero Application dependency) while retaining `ConnectToPeerAsync` for known peers.

```csharp
// Percolator.PluginSdk/IPeerConnectionService.cs
public interface IPeerConnectionService
{
    Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);

    Task<DomainResult<PeerConnectionResult>> ConnectWithInvitationAsync(
        PublicIdentityId ownerId,
        PeerInvitation invitation,
        CancellationToken ct = default);
}
```

### 1.3 Additions & Modifications — `Percolator.Domain`
- **Add** `Percolator.Domain/Identities/ValueObjects/PeerInvitation.cs`:
  - Immutable value object for out-of-band pinned-identity invitations (matching Signal's default protocol: **never** embeds a `DeliveryToken`).
  - Carries strictly 4 non-ephemeral fields: `InviterIdentityId`, `InviterIdentityKey` (32-byte Ed25519 public key), `RelayEndpoint` (host + optional port or port range), `RoutingToken`.
  - Embeds zero `SignedPreKey`, `OneTimePreKey`, or `DeliveryToken` material (survives 48h/7d SPK rotations in `PreKeyMaintenanceService` indefinitely and grants every connecting peer a fresh 4-DH OTPK from the shared Exchange).
  - Enforces MitM resistance via `VerifyFetchedBundle(PreKeyBundle fetchedBundle, ICryptoEngine cryptoEngine)`: verifies constant-time equality of `fetchedBundle.IdentityKey` against pinned `InviterIdentityKey` AND verifies Ed25519 signature over `fetchedBundle.SignedPreKey`.

```csharp
// Percolator.Domain/Identities/ValueObjects/PeerInvitation.cs
namespace Percolator.Domain.Identities.ValueObjects;

using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.Ports;

public sealed record PeerInvitation
{
    public PublicIdentityId InviterIdentityId { get; }
    public IdentityKey InviterIdentityKey { get; }
    public RelayEndpoint RelayEndpoint { get; }
    public BlindedRoutingToken RoutingToken { get; }

    private PeerInvitation(
        PublicIdentityId inviterIdentityId,
        IdentityKey inviterIdentityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken)
    {
        InviterIdentityId = inviterIdentityId;
        InviterIdentityKey = inviterIdentityKey;
        RelayEndpoint = relayEndpoint;
        RoutingToken = routingToken;
    }

    public static DomainResult<PeerInvitation> Create(
        PublicIdentityId inviterIdentityId,
        IdentityKey inviterIdentityKey,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken)
    {
        if (!inviterIdentityId.IsValid)
            return DomainResult<PeerInvitation>.Failure(new DomainError("INVALID_IDENTITY_ID", "InviterIdentityId cannot be empty."));
        if (inviterIdentityKey is null)
            return DomainResult<PeerInvitation>.Failure(new DomainError("MISSING_IDENTITY_KEY", "InviterIdentityKey is required to pin peer identity."));
        if (relayEndpoint is null)
            return DomainResult<PeerInvitation>.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "RelayEndpoint is required."));
        if (!routingToken.IsValid)
            return DomainResult<PeerInvitation>.Failure(new DomainError("INVALID_ROUTING_TOKEN", "RoutingToken cannot be empty."));

        return DomainResult<PeerInvitation>.Success(
            new PeerInvitation(inviterIdentityId, inviterIdentityKey, relayEndpoint, routingToken));
    }

    public DomainResult VerifyFetchedBundle(PreKeyBundle fetchedBundle, ICryptoEngine cryptoEngine)
    {
        ArgumentNullException.ThrowIfNull(cryptoEngine);
        if (fetchedBundle is null)
            return DomainResult.Failure(new DomainError("MISSING_PREKEY_BUNDLE", "Fetched PreKeyBundle cannot be null."));
        if (!fetchedBundle.DeviceId.IsValid)
            return DomainResult.Failure(new DomainError("INVALID_DEVICE_ID", "DeviceId in fetched PreKeyBundle must be valid."));
        if (fetchedBundle.IdentityKey is null || fetchedBundle.SignedPreKey is null || fetchedBundle.SignedPreKeySignature is null)
            return DomainResult.Failure(new DomainError("NULL_KEY_MATERIAL", "IdentityKey, SignedPreKey, and signature cannot be null."));

        if (!CryptographicOperations.FixedTimeEquals(InviterIdentityKey.Span, fetchedBundle.IdentityKey.Span))
            return DomainResult.Failure(new DomainError("IDENTITY_KEY_MISMATCH", "Fetched PreKeyBundle IdentityKey does not match pinned invitation IdentityKey."));

        var valid = cryptoEngine.VerifyEd25519Signature(
            InviterIdentityKey,
            fetchedBundle.SignedPreKey.Span,
            fetchedBundle.SignedPreKeySignature.Span);

        return valid
            ? DomainResult.Success()
            : DomainResult.Failure(new DomainError("INVALID_PREKEY_SIGNATURE", "Signed pre-key signature verification failed against pinned identity key."));
    }
}
```

- **Add** `Percolator.Domain/Identities/Events/ContactOutboundAcceptedDomainEvent.cs`:
  - Carries `PeerTrustLevel TrustLevel` for symmetry with `ContactRequestApprovedDomainEvent`.

```csharp
// Percolator.Domain/Identities/Events/ContactOutboundAcceptedDomainEvent.cs
namespace Percolator.Domain.Identities.Events;

using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;

public sealed record ContactOutboundAcceptedDomainEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    PeerTrustLevel TrustLevel,
    DateTimeOffset OccurredOnUtc) : IDomainEvent;
```

- **Modify** `Percolator.Domain/Identities/Model/PeerContact.cs`:
  - `MarkOutboundAccepted(IDateTimeProvider timeProvider)`: emit `ContactOutboundAcceptedDomainEvent(OwnerIdentityId, RemotePeerId, TrustLevel, timeProvider.UtcNow)` on `PendingOutbound -> Active` transition (idempotent no-op when already `Active`).
- **Modify** `Percolator.Domain/Channels/ValueObjects/ChannelId.cs` & `Percolator.Domain/Channels/Model/DirectChannel.cs`:
  - Scope 1:1 `ChannelId` strictly per `(Owner, Peer, Exchange)` (`ChannelId.DeterministicForDirect(PublicIdentityId ownerId, PublicIdentityId remotePeerId, RelayEndpoint relayEndpoint)`) so two peers who share mailboxes on multiple Exchanges (e.g., `Friends-Exchange` and `Work-Exchange`) maintain separate channels, separate Double Ratchet sessions, and guaranteed same-Exchange replies (aligned with `shell-flow.md`).
  - Add `UpdateRelayEndpoint(RelayEndpoint relayEndpoint, IDateTimeProvider timeProvider)` (rejects null `relayEndpoint`, updates `RelayEndpoint` and `LastActivityUtc`).
  - `Activate(IDateTimeProvider timeProvider, RelayEndpoint? relayEndpoint = null)`:
    - If `relayEndpoint != null`, assign `RelayEndpoint = relayEndpoint`.
    - If `RelayEndpoint is null`, fail with `DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "DirectChannel cannot be activated without a RelayEndpoint."))`.
    - If already `Active`, return `DomainResult.Success()`.
    - Transition `Status = DirectChannelStatus.Active`, update `LastActivityUtc = timeProvider.UtcNow`, emit `DirectChannelActivatedDomainEvent(Id, OwnerIdentityId, RemotePeerId, RelayEndpoint, LastActivityUtc)`.
- **Modify** `Percolator.Domain/Channels/Model/DirectChannelStatus.cs`:
  - Retain `Active = 0`, `PendingAcceptance = 1`. (For 1:1 outbound relayed channels, status is initialized as `DirectChannelStatus.PendingAcceptance` until the remote peer replies, mirroring `ContactState.PendingOutbound`).
- **Modify** `Percolator.Domain/Delivery/Hosting/RelayHostingPolicy.cs`:
  - Replace minimal policy with operator-controlled Exchange registration governance (`RelayRegistrationStrategy`: `ApprovalRequired`, `Open`, `Closed`; `bool AutoApproveLocalhost`; `RelayFeatureFlags`; quotas; TTL).
  - Add `ToPurgePolicy()` to configure `RelayMailboxQueue` with matching envelope TTL without concept duplication.
  - Update `RelayHostedPreKeyBundle.cs` to validate `policy.IsFeatureEnabled(RelayFeatureFlags.PreKeys)`.

```csharp
// Percolator.Domain/Delivery/Hosting/RelayHostingPolicy.cs
namespace Percolator.Domain.Delivery.Hosting;

using Percolator.Domain.Delivery.ValueObjects;

public enum RelayRegistrationStrategy
{
    ApprovalRequired = 0, // Stage incoming mailbox registration requests for Exchange operator Approve/Reject
    Open = 1,             // Auto-approve incoming mailbox registrations up to MaxMailboxes
    Closed = 2            // Silently hang up on all new registration requests; existing mailboxes remain active
}

[Flags]
public enum RelayFeatureFlags
{
    None = 0,
    PreKeys = 1 << 0,
    Profiles = 1 << 1,
    Blobs = 1 << 2,
    Groups = 1 << 3,
    Discovery = 1 << 4,
    All = PreKeys | Profiles | Blobs | Groups | Discovery
}

public sealed record RelayHostingPolicy
{
    public const int DefaultMaxOneTimePreKeys = 100;
    public const int DefaultMaxMailboxes = 1024;
    public const long DefaultMaxMailboxBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan DefaultEnvelopeTtl = TimeSpan.FromDays(14);

    public RelayRegistrationStrategy RegistrationStrategy { get; init; }
    public bool AutoApproveLocalhost { get; init; }
    public RelayFeatureFlags EnabledFeatures { get; init; }
    public int MaxMailboxes { get; init; }
    public long MaxMailboxBytes { get; init; }
    public TimeSpan EnvelopeTtl { get; init; }
    public int MaxOneTimePreKeysPerIdentity { get; init; }

    public bool IsAcceptingPreKeys => IsFeatureEnabled(RelayFeatureFlags.PreKeys);

    public bool IsFeatureEnabled(RelayFeatureFlags flag) => (EnabledFeatures & flag) == flag;

    public PurgePolicy ToPurgePolicy() => new(
        DefaultTtl: EnvelopeTtl,
        MaxRetainedEnvelopes: MaxMailboxes * 100,
        MaxEnvelopeSizeBytes: PurgePolicy.DefaultMaxEnvelopeSizeBytes);

    public RelayHostingPolicy(
        RelayRegistrationStrategy registrationStrategy = RelayRegistrationStrategy.Open,
        bool autoApproveLocalhost = true,
        RelayFeatureFlags enabledFeatures = RelayFeatureFlags.All,
        int maxMailboxes = DefaultMaxMailboxes,
        long maxMailboxBytes = DefaultMaxMailboxBytes,
        TimeSpan? envelopeTtl = null,
        int maxOneTimePreKeysPerIdentity = DefaultMaxOneTimePreKeys)
    {
        if (maxMailboxes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxes));
        if (maxMailboxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxBytes));
        var ttl = envelopeTtl ?? DefaultEnvelopeTtl;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(envelopeTtl));
        if (maxOneTimePreKeysPerIdentity <= 0) throw new ArgumentOutOfRangeException(nameof(maxOneTimePreKeysPerIdentity));

        RegistrationStrategy = registrationStrategy;
        AutoApproveLocalhost = autoApproveLocalhost;
        EnabledFeatures = enabledFeatures;
        MaxMailboxes = maxMailboxes;
        MaxMailboxBytes = maxMailboxBytes;
        EnvelopeTtl = ttl;
        MaxOneTimePreKeysPerIdentity = maxOneTimePreKeysPerIdentity;
    }

    public static RelayHostingPolicy Default { get; } = new();
    public static RelayHostingPolicy HostManaged { get; } = new(
        registrationStrategy: RelayRegistrationStrategy.ApprovalRequired,
        autoApproveLocalhost: true,
        enabledFeatures: RelayFeatureFlags.PreKeys | RelayFeatureFlags.Profiles | RelayFeatureFlags.Blobs | RelayFeatureFlags.Groups);
}
```

### 1.3b Additional Domain Projects Analysis & Refactoring
- **`Percolator.Domain.Kernel`**:
  - No code modifications required. Verified: contains only foundational DDD abstractions (`AggregateRoot`, `DomainResult`, `IDateTimeProvider`, `IDomainEvent`, `PublicIdentityId`). Zero transport, routing, or P2P concepts.
- **`Percolator.Apps.FileTransfer.Domain` & `Percolator.Apps.FileTransfer.Domain.Tests`**:
  - No code modifications required. Verified: models Merkle trees, chunk slices, bitfields, and swarm members. Does NOT reference `Percolator.Domain` or `DeliveryRoute`. Purely transport-agnostic (1:1 control plane signaling route changes occur strictly in `Percolator.Apps.FileTransfer` at Step 3).
- **`Percolator.Domain.IntegrationTests`**:
  - References `Percolator.Domain` directly (pure in-memory domain aggregate interaction testing: `X3dhAgreement`, `DirectRatchetSession`, `RelayHostedPreKeyBundle`, `RelayGroupLedger`).
  - **Modify** `Builders/ScenarioParticipant.cs`: add `CreatePeerInvitation(RelayEndpoint relayEndpoint)` returning valid `PeerInvitation` with pinned `IdentityKey` (zero `DeliveryToken`).
  - **Modify** `Builders/ScenarioSessionPairBuilder.cs`: add `ViaPinnedPeerInvitation(PeerInvitation invitation, RelayHostedPreKeyBundle hostedBundle)` consuming a fresh OTPK from `hostedBundle`, verifying via `invitation.VerifyFetchedBundle`, and establishing mutual `DirectRatchetSession` pairs via full 4-DH X3DH.
  - **Add** `PinnedInvitationScenarioTests.cs`:
    - `PinnedInvitation_MultiplePeers_ConsumeDistinctOneTimePreKeys_Full4DhForwardSecrecy` (Alice and Carol independently consume distinct OTPKs from Bob's `RelayHostedPreKeyBundle` using Bob's single invitation; both achieve symmetric ratchet communication with Bob).
    - `PinnedInvitation_MaliciousRelaySwapsIdentityKey_RejectedBeforeSessionCreation` (Exchange returns a bundle with an attacker's `IdentityKey` → `VerifyFetchedBundle` fails with `IDENTITY_KEY_MISMATCH`).
    - `PinnedInvitation_MaliciousRelayForgesSignedPreKey_RejectedBeforeSessionCreation` (Exchange returns Bob's `IdentityKey` with a forged SPK → `VerifyFetchedBundle` fails with `INVALID_PREKEY_SIGNATURE`).
    - `PinnedInvitation_SpkRotation_OldInvitationContinuesWorkingWithFreshSpk` (Bob rotates SPK on `RelayHostedPreKeyBundle`; Alice still establishes a session using Bob's original invitation link).

### 1.4 Unit & Domain Integration Tests
- Delete tests referencing Direct P2P enums in `Percolator.Domain.Tests`. Retain `PortRangeTests` verifying canonical dynamic range bounds (5100-5199), single port invariants, and boundary checks.
- Add/update tests in `Percolator.Domain.Tests`:
  - `PeerInvitationTests`:
    - `Create_WithInvalidIdentityIdOrNullIdentityKey_FailsValidation`
    - `Create_WithNullRelayEndpointOrEmptyRoutingToken_FailsValidation`
    - `VerifyFetchedBundle_MatchingPinnedKeyAndValidSpkSignature_ReturnsSuccess` (using real `SignalCryptoEngine`)
    - `VerifyFetchedBundle_MismatchedIdentityKey_ReturnsIdentityKeyMismatchFailure`
    - `VerifyFetchedBundle_TamperedSpkSignature_ReturnsInvalidPreKeySignatureFailure`
  - `ChannelIdTests`:
    - `DeterministicForDirect_SamePeersDifferentExchanges_ProducesDistinctChannelIds`
  - `PeerContactTests`:
    - `MarkOutboundAccepted_PendingOutbound_TransitionsToActive_EmitsContactOutboundAcceptedDomainEvent` (asserts `TrustLevel == PeerTrustLevel.Tofu`)
    - `MarkOutboundAccepted_AlreadyActive_IsIdempotent_EmitsNoEvent`
    - `MarkOutboundAccepted_BlockedOrPendingApproval_FailsWithInvalidStateTransition`
  - `DirectChannelTests`:
    - `Activate_WhenNoRelayEndpointExistsOrPassed_ReturnsMissingRelayEndpointFailure`
    - `Activate_WithRelayEndpoint_TransitionsToActive_EmitsDirectChannelActivatedDomainEvent`
    - `UpdateRelayEndpoint_NullEndpoint_Fails`
    - `UpdateRelayEndpoint_ValidEndpoint_UpdatesEndpointAndActivityUtc`
  - `RelayHostingPolicyTests`:
    - `ToPurgePolicy_DerivesPurgePolicyMatchingEnvelopeTtl`
    - `RelayHostingPolicy_RejectsNonPositiveQuotasAndTtl`
    - `RelayHostedPreKeyBundle_Create_WhenPreKeyFeatureDisabled_RejectsPublish`
- Execute and verify `Percolator.Domain.IntegrationTests` (all 9 existing scenario tests + new `PinnedInvitationScenarioTests`).
- Execute and verify `Percolator.Apps.FileTransfer.Domain.Tests` (57 tests pass 100%).

### 1.5 Build Gate
- `dotnet build Percolator.Domain.Kernel` succeeds.
- `dotnet test Percolator.Domain.Tests` passes 100%.
- `dotnet test Percolator.Domain.IntegrationTests` passes 100%.
- `dotnet test Percolator.Apps.FileTransfer.Domain.Tests` passes 100%.
- `dotnet build Percolator.PluginSdk` succeeds.
- Outer projects (`Percolator.Application`, `Percolator.Infrastructure`, `Percolator.Desktop`, `Percolator.Simulator`) expected to fail build.

---

## Step 2: Critical Review & Remediation — Domain & PluginSdk

### 2.1 Audit Checklist
- Zero references to `DirectP2P`, `DirectPeer`, `IDirectEndpointRegistry`, `PortRangePrivacyPreset`, or `allowDirectEndpoint` in `Percolator.Domain`, `Percolator.Domain.Kernel`, `Percolator.Domain.IntegrationTests`, and `Percolator.PluginSdk`.
- Zero infrastructure/serialization/transport types leaked into `Percolator.Domain`, `Percolator.Domain.Kernel`, or `Percolator.PluginSdk`.
- Zero nullable required constructor/method parameters (`IDateTimeProvider`, `ICryptoEngine`, `RelayEndpoint`, `IdentityKey` on `PeerInvitation`).
- Cryptographic verification uses `ICryptoEngine` port exclusively (zero external or ad-hoc crypto adapters in Domain).
- `DirectChannelStatus` values strictly validated (`Active`, `PendingAcceptance`).
- All domain events implement `IDomainEvent` and carry strongly-typed Domain IDs/keys.
- `PeerInvitation` invariants enforced at construction (no invalid state representable); `VerifyFetchedBundle` enforces constant-time pinned `IdentityKey` equality and Ed25519 SPK signature verification.
- `RelayHostingPolicy` exposes typed flags/enums and `ToPurgePolicy()` (no stringly-typed policy keys or divergent TTLs).
- Verify `Percolator.Domain.IntegrationTests` exercises `PeerInvitation` + `RelayHostedPreKeyBundle` multi-peer 4-DH ratcheting and MitM rejection with real cryptographic math.
- Test audit: remove tautological property-getter tests or brittle reflection/mock-call-count assertions; retain only invariant/state-transition/crypto-contract tests.
- Re-run all domain test suites (`Domain.Tests`, `Domain.IntegrationTests`, `FileTransfer.Domain.Tests`) → 100% green before Step 3.

---

## Step 3: Application & Plugin Apps Refactor

### 3.1 Deletions
- `Percolator.Application/Ports/IStreamRegistry.cs`
- `Percolator.Application/Ports/IStreamRegistryManagement.cs`
- `Percolator.Application/Invitations/PortRangePrivacyPreset.cs`
- `Percolator.Application/Ports/IBlobStoragePorts.cs`: remove `IPeerStreamBlobClient`.
- `Percolator.Application/Ports/ISealedEnvelopeUnwrapper.cs`: remove `UnwrapDirect(...)` (all inbound delivery is envelope-mediated via `Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId)`).
- (Retain `PortRangeStart` and `PortRangeEnd` in `Percolator.Application/Ports/IVaultSettingsStore.cs` for configuring the local host's relay binding range).

### 3.2 Modifications — `Percolator.Application`
- `Percolator.Application/Delivery/DeliveryRoute.cs`:
  - Remove `DeliveryRouteType.DirectP2P` and `Uri? DirectEndpoint`.
  - Make `BlindedRoutingToken TargetToken` and `RelayEndpoint RelayEndpoint` non-nullable.

```csharp
// Percolator.Application/Delivery/DeliveryRoute.cs
namespace Percolator.Application.Delivery;

using Percolator.Domain.Delivery.ValueObjects;

public enum DeliveryRouteType
{
    RelayedOneToOne = 2,
    RelayedGroup = 3
}

public readonly record struct DeliveryRoute(
    DeliveryRouteType Type,
    BlindedRoutingToken TargetToken,
    RelayEndpoint RelayEndpoint,
    DeliveryToken? TargetDeliveryToken = null);
```

- `Percolator.Application/Routing/RoutingCoordinator.cs`:
  - Remove `DeliveryRoutingMode.DirectPeer` and `IStreamRegistry` dependency.
  - Resolve exclusively via `IPeerMailboxRoutingRepository.GetRouteForExchangeAsync(ownerId, targetPeerId, relayEndpoint, _timeProvider.UtcNow, ct)` → `DeliveryRoutingMode.Relayed` or failure `NO_RELAY_ROUTE_AVAILABLE` (Option 3A: routes are strictly scoped per `(Owner, Peer, Exchange)` so replies never cross Exchanges).
- `Percolator.Application/Ports/IPreKeyBundleProvider.cs`:
  - Add authenticated shared-Exchange fetch method so `OutboundConnectionWorkflow` can fetch and cryptographically verify a peer's `PreKeyBundle` **before** persisting any routing/contact state to SQLite (Choice A — Exact Signal Default):
    ```csharp
    Task<DomainResult<PreKeyBundle>> FetchAuthenticatedAsync(
        PublicIdentityId callerOwnerId,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken targetRoutingToken,
        DeviceId? deviceId = null,
        CancellationToken ct = default);
    ```
- `Percolator.Application/Invitations/PeerInvitationPayload.cs` & `PeerInvitationCodec.cs`:
  - Replace `PeerInvitationPayload` fields (`EndpointHosts`, `PortRange`, nullable `RelayEndpoint`) with Pinned-Identity Invitation fields mirroring `PeerInvitation` (zero `DeliveryToken`):
    - `PublicIdentityId IdentityId`
    - `IdentityKey IdentityKey` (32-byte Ed25519 pinned public key)
    - `RelayEndpoint RelayEndpoint` (mandatory — host + optional port or port range)
    - `BlindedRoutingToken RoutingToken`
  - Add `ToDomain(): DomainResult<PeerInvitation>` and `FromDomain(PeerInvitation)`.
  - Binary codec (`PeerInvitationCodec`) single replacement format (`DEC-CANONICAL-URI-SCHEME`, zero legacy clients):
    - Encodes and decodes strictly as `percolator://invite/<base64url>` (compact fixed binary framing, zero JSON).
    - Delete the `percolator://invite?id=...` query-string format completely (no `/v2` path versioning and no legacy query-string fallback).
    - Wire layout (little-endian length prefix):
      `[formatByte:1B=0x01][identityId:16B][identityKey:32B][routingToken:16B][relayEndpointLen:2B LE][relayEndpointUtf8]`
    - Reject empty/invalid `RelayEndpoint`, malformed lengths, unknown format byte, or zeroed/invalid identity/token bytes at decode time.
- **Add** `Percolator.Application/Invitations/IPeerInvitationService.cs` & `PeerInvitationService.cs`:
  - Signature:
    ```csharp
    public interface IPeerInvitationService
    {
        Task<DomainResult<PeerInvitation>> CreatePeerInvitationAsync(
            PublicIdentityId ownerId,
            RelayEndpoint relayEndpoint,
            CancellationToken ct = default);
    }
    ```
  - Implementation (`PeerInvitationService`): injects non-nullable `IIdentityProfileRepository` and `IRelayDirectory`. Loads owner's `IdentityProfile` (`IdentityKey`), resolves matching `RelayDirectoryEntry` by `relayEndpoint`, reads `MailboxTokenTriad` via `IRelayDirectory.GetMailboxTokensAsync(ownerId, entry.RelayIdentityId, ct)` (fails cleanly with `NO_RELAY_MAILBOX_LINKED` if unlinked), and returns `PeerInvitation.Create(ownerId, profile.IdentityKey, relayEndpoint, triad.RoutingToken)` (never exposes `DeliveryToken` in an invitation).
  - Register `IPeerInvitationService` → `PeerInvitationService` in Application/Infrastructure DI.
- **Add** `Percolator.Application/Hosting/IExchangeRegistrationModerationService.cs`:
  - Operator-only driving port for reviewing, approving, rejecting, or blocking pending mailbox registration requests on a local Self-Hosted Exchange when `RegistrationStrategy == RelayRegistrationStrategy.ApprovalRequired`.
- **Add** `Percolator.Application/Profiles/Events/ContactOutboundAcceptedEvent.cs`:

```csharp
// Percolator.Application/Profiles/Events/ContactOutboundAcceptedEvent.cs
namespace Percolator.Application.Profiles.Events;

using Percolator.Application.Events;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.Model;

[MapFromDomainEvent(typeof(ContactOutboundAcceptedDomainEvent))]
public sealed partial record ContactOutboundAcceptedEvent : IApplicationEvent
{
    public required PublicIdentityId OwnerIdentityId { get; init; }
    public required PublicIdentityId RemotePeerId { get; init; }
    public required PeerTrustLevel TrustLevel { get; init; }
    public required DateTimeOffset OccurredOnUtc { get; init; }
}
```

- `Percolator.Application/Handshake/HandshakeService.cs`:
  - Make `ISessionWirePacker` and `IRoutingCoordinator` **non-nullable required** constructor parameters (remove `= null` defaults and fallback branches).
  - Harden existing `InitiateHandshakeAsync(ownerId, ownerDeviceId, remotePeerId, relayEndpoint, remoteBundle, initialAppId, initialPayload, ct)`:
    - Executes `X3dhAgreement.Initiate` against the verified, relay-fetched `remoteBundle` (full 4-DH with OTPK when available, 3-DH fallback only if the Exchange's OTPK pool is exhausted).
    - Frames initial reply payload if provided (`[appId:1B][payloadBytes]`, carrying the invitee's `DeliveryToken` and `RoutingToken` on `relayEndpoint`), applies bucketed payload padding (`SYS-PAYLOAD-LENGTH-PADDING`), steps the sending chain to derive message key, encrypts via `_cryptoEngine.EncryptAesGcm`, and zeroizes plaintext/key buffers immediately via `CryptographicOperations.ZeroMemory` (`SYS-DETERMINISTIC-ZEROIZATION`).
    - Persists Exchange-scoped Double Ratchet session (`_sessionRepo.SaveSessionAsync`, keyed by `ChannelId.DeterministicForDirect(ownerId, remotePeerId, relayEndpoint)`) and enqueues `OutboxJob` (`ChannelId.DeterministicForDirect(ownerId, remotePeerId, relayEndpoint)`, never `ChannelId.New()`, with `DeliveryRoute(DeliveryRouteType.RelayedOneToOne, targetRoutingToken, relayEndpoint, TargetDeliveryToken: null)` for authenticated first-contact delivery per Choice A) inside a **single atomic transaction** (`SYS-ATOMIC-STATE-PERSISTENCE`).
  - Replace all `Array.Clear(...)` calls on cryptographic secrets/plaintexts (e.g., `HandshakeService.cs#L387`) with `CryptographicOperations.ZeroMemory(...)`, and zeroize `innerPlaintext` buffers in `finally` blocks (`SYS-DETERMINISTIC-ZEROIZATION`).
  - In `CompletePendingHandshakeAsync` (invoked **during** `InboundConnectionWorkflow.AcceptRequestAsync`, after contact approval and prior to activating `DirectChannel`): decrypts the staged inbound initial payload carrying the sender's `ProfileCapabilityDto` (including the invitee's `DeliveryToken` for that Exchange), unpads, and dispatches to `_appRouter.Resolve(appId).HandleInboundAsync` so the sender's reply `PeerMailboxRoutingCapability` is persisted for `(ownerId, senderId, relayEndpoint)` before `DirectChannel.Activate` executes.
- `Percolator.Application/Connections/IOutboundConnectionWorkflow.cs` & `OutboundConnectionWorkflow.cs`:
  - Update interface to require the local initiator's identity:
    ```csharp
    public interface IOutboundConnectionWorkflow
    {
        Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
            PublicIdentityId ownerId,
            PeerInvitationPayload payload,
            TimeSpan probeTimeout,
            CancellationToken ct = default);
    }
    ```
  - Delete direct URI builder, `?? "127.0.0.1"`, `?? 5100`, direct socket probing, and `IStreamRegistryManagement`.
  - Order of operations (strictly enforces verify-before-commit, re-invitation safety & 1-RTT authenticated shared-Exchange PreKey fetch):
    1. Validate `payload.ToDomain()` → `PeerInvitation`.
    2. Verify `ownerId` has an active registered mailbox on `invitation.RelayEndpoint` via `IRelayDirectory` (fail cleanly with `EXCHANGE_NOT_LINKED` if not yet a member of that shared Exchange).
    3. **Re-invitation safety check** (`_contactRepo.GetByIdAsync(ownerId, invitation.InviterIdentityId, ct)`):
       - If existing contact is `ContactState.Blocked` → fail immediately with `CONTACT_BLOCKED` (zero DB writes, zero network calls).
       - If existing contact exists with a different `IdentityKey` (`!CryptographicOperations.FixedTimeEquals(...)`) → fail immediately with `IDENTITY_KEY_CHANGED` (`SYS-KEY-CHANGE-TRUST-HALT` / `DEP-DESKTOP-KEY-CHANGE-WARNING`, zero DB writes, zero network calls).
    4. Fetch consumable `PreKeyBundle` via `IPreKeyBundleProvider.FetchAuthenticatedAsync(ownerId, invitation.RelayEndpoint, invitation.RoutingToken, DeviceId.Primary, cts.Token)` bounded by `probeTimeout` (authenticates using `ownerId`'s Exchange mailbox credentials over the authenticated HTTP/2 pool per Choice A). On transient network failure or timeout, return `OutboundConnectionStatus.PendingOffline` cleanly with zero DB writes.
    5. Verify via `invitation.VerifyFetchedBundle(fetchedBundle, _cryptoEngine)` — abort immediately on `IDENTITY_KEY_MISMATCH` or `INVALID_PREKEY_SIGNATURE` with **zero DB writes** (prevents malicious Exchange MitM).
    6. Persist initial `PeerMailboxRoutingCapability` (`TargetDeliveryToken = null` until inviter's E2EE approval reply supplies it) scoped to `(ownerId, invitation.InviterIdentityId, invitation.RelayEndpoint)` via `IPeerMailboxRoutingRepository.SaveAsync`.
    7. Create or preserve `PeerContact` (`ContactState.PendingOutbound` if new) with pinned `invitation.InviterIdentityKey` via `_contactRepo.SaveAsync`.
    8. Ensure Exchange-scoped `DirectChannel` exists via `_channelRepo.SaveDirectAsync` with `ChannelId.DeterministicForDirect(ownerId, invitation.InviterIdentityId, invitation.RelayEndpoint)`, `DirectChannelStatus.PendingAcceptance`, and `invitation.RelayEndpoint`.
    9. Build initial reply capability payload via `IInitialHandshakePayloadProvider.CreateInitialPayloadAsync(ownerId, channelId, ct)` (emitting `ownerId`'s `RoutingToken` + `DeliveryToken` on `invitation.RelayEndpoint`, zero `allowDirectEndpoint` flag).
    10. Call `IHandshakeService.InitiateHandshakeAsync(ownerId, DeviceId.Primary, invitation.InviterIdentityId, invitation.RelayEndpoint, fetchedBundle, initialAppId, initialPayload, ct)` to execute 4-DH X3DH, persist Exchange-scoped initial ratchet session, and enqueue the authenticated initial PreKeyMessage into `IOutboxRepository` atomically.
    11. Return `DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(OutboundConnectionStatus.Connected, invitation.InviterIdentityId, payload))`. (Asynchronous delivery of the PreKeyMessage is guaranteed by `OutboxDispatchHost`).
- `Percolator.Application/Services/PeerConnectionService.cs`:
  - Delete `ConnectToDirectPeerAsync` and `IStreamRegistryManagement` dependency.
  - Implement `ConnectWithInvitationAsync(ownerId, invitation, ct)` delegating to `_outboundWorkflow.ConnectAsync(ownerId, PeerInvitationPayload.FromDomain(invitation), TimeSpan.FromSeconds(10), ct)`.
  - Keep `ConnectToPeerAsync` for known peers (fetches consumable bundle via `IPreKeyBundleProvider.FetchConsumableBundleAsync` on the target Exchange and initiates via `IHandshakeService.InitiateHandshakeAsync`).
- `Percolator.Application/Connections/InboundConnectionWorkflow.cs` & `Percolator.Application/Ingress/InboundIngressPipeline.cs`:
  - In `InboundIngressPipeline.cs`: when an inbound message is received from a peer whose contact state is `ContactState.PendingOutbound`, invoke `contact.MarkOutboundAccepted(_timeProvider)`, persist via `_contactRepo.SaveAsync`, and publish `ContactOutboundAcceptedDomainEvent` → `ContactOutboundAcceptedEvent`. When the encrypted reply contains `ProfileCapabilityDto` carrying the inviter's `DeliveryToken` for that Exchange, persist it to `IPeerMailboxRoutingRepository` so **all subsequent outbound messages** switch automatically from authenticated initial delivery to **unauthenticated Sealed Sender**.
  - In `InboundConnectionWorkflow.AcceptRequestAsync`: invoke `CompletePendingHandshakeAsync` during approval to decrypt the initial payload and store the invitee's reply `PeerMailboxRoutingCapability` (including invitee's `DeliveryToken`) for `(ownerId, requesterId, relayEndpoint)`; verify `IPeerMailboxRoutingRepository` contains the peer's reply capability on that Exchange; activate `DirectChannel` with `channel.Activate(_timeProvider, peerRoute.RelayEndpoint)` (fail if missing relay endpoint); dispatch outbound E2EE `ProfileCapabilityDto` (including inviter's `DeliveryToken` on that Exchange) via `DeliveryRoute.RelayedOneToOne` using unauthenticated Sealed Sender on the same Exchange.
- `Percolator.Application/Egress/OutboundEgressPipeline.cs`:
  - Delete `IStreamRegistry` dependency and Phase 5 `DirectP2P` fast-path.
  - Apply bucketed payload padding (`SYS-PAYLOAD-LENGTH-PADDING`) prior to encryption and zeroize plaintext/message-key buffers with `CryptographicOperations.ZeroMemory` (`SYS-DETERMINISTIC-ZEROIZATION`).
  - Persist Exchange-scoped Double Ratchet state progression and `OutboxJob` enqueue inside a **single atomic transaction** (`SYS-ATOMIC-STATE-PERSISTENCE`).
  - All `RelayedOneToOne` sends resolve `PeerMailboxRoutingCapability` strictly for the channel's `RelayEndpoint` (`IPeerMailboxRoutingRepository.GetRouteForExchangeAsync(ownerId, peerId, channel.RelayEndpoint, ...)`) and enqueue into `IOutboxRepository` with `DeliveryRoute(DeliveryRouteType.RelayedOneToOne, cap.RoutingToken, cap.RelayEndpoint, cap.DeliveryToken)`.
  - All `RelayedGroup` sends enqueue with `DeliveryRoute(DeliveryRouteType.RelayedGroup, groupRoutingToken, groupRelayEndpoint, groupDeliveryToken)`.
- `Percolator.Application/Services/BlobStorageService.cs`:
  - Delete `IStreamRegistry` and `IPeerStreamBlobClient` dependencies and `BlobStorageMode.DirectP2P` upload/download branches.
  - Route exclusively via `RelayHosted` or `P2PSwarm`.
- `Percolator.Application/Services/GroupKeyDistributionService.cs`:
  - Change line 97 `DeliveryRoute.DirectP2P` → `DeliveryRoute.RelayedOneToOne`.

### 3.3 Modifications — `Percolator.Apps.*` (Chat, FileTransfer, Discovery)
- **`Percolator.Apps.Chat`**:
  - `Features/Profiles/ProfileCapabilityDto.cs`: change default `SourceType` from `ProfileSourceType.DirectP2P` to `ProfileSourceType.Relay`.
  - `Features/Profiles/ChatInitialPayloadProvider.cs`:
    - Remove `bool allowDirectEndpoint` parameter and `INetworkEndpointDiscoveryPort` dependency.
    - Always emit `ProfileSourceType.Relay` with sender's linked mailbox coordinates for the channel's specific `RelayEndpoint` (`BlindedRoutingToken`, `DeliveryToken`, and `ProfileLocator`; fail cleanly if owner has no linked mailbox on that Exchange).
  - `Ingress/ChatPayloadHandler.cs`:
    - Remove `IDirectEndpointRegistry` dependency and `ProfileSourceType.DirectP2P` branch.
    - On `HandleProfileCapabilityAsync`: persist `PeerMailboxRoutingCapability` keyed by `(ownerId, senderId, replyRelayEndpoint)` (storing the peer's `DeliveryToken` to enable unauthenticated Sealed Sender on all subsequent messages) and update `DirectChannel.UpdateRelayEndpoint(replyRelayEndpoint, _timeProvider)` if channel exists.
    - Line 364: replace `DeliveryRoute.DirectP2P` → `DeliveryRoute.RelayedOneToOne`.
  - `Features/Messaging/ChatService.cs`:
    - Lines 438-439: replace `DirectP2P` ternary with `ProfileSourceType.Relay` / `"relay"`.
  - `Percolator.Apps.Chat.Tests`:
    - Update `GroupInvitationServiceTests.cs` (lines 86, 126, 168), `ChatServiceTests.cs` (lines 183, 218, 243, 276, 311, 339, 378, 411), `ChatPayloadHandlerTests.cs` (line 710), and `ChatTestFactory.cs` (line 59) from `DeliveryRoute.DirectP2P` to `DeliveryRoute.RelayedOneToOne`.
- **`Percolator.Apps.FileTransfer`**:
  - `Features/TransferNegotiation/FileTransferActionHandler.cs`:
    - Replace `DeliveryRoute.DirectP2P` (lines 257, 395, 478) → `DeliveryRoute.RelayedOneToOne` for all 1:1 control-plane messages (transfer proposals, acceptances, cancellations).
    - Retain `Percolator.Apps.FileTransfer/Ports/INetworkEndpointDiscoveryPort.cs` and implementations for peer swarm chunk transfer data-plane.
  - `Percolator.Apps.FileTransfer.Tests`:
    - Update all action handler tests to assert `DeliveryRoute.RelayedOneToOne` on control signaling.
- **`Percolator.Apps.Discovery`**:
  - `Features/NearbyPeerDiscovery/PeerDiscoveryDescriptor.cs`:
    - Ensure `Route` defaults to `DeliveryRoute.RelayedOneToOne`.
  - `Ingress/DiscoveryPayloadHandler.cs`:
    - Ensure all candidate announcements advertise relay routes (lines 215, 290 already use `RelayedOneToOne`).
  - `Percolator.Apps.Discovery.Tests`:
    - In `NearbyPeerDiscoveryWorkflowTests.cs` (line 186): delete the unused `ConnectToDirectPeerAsync` mock method on the test-double `IPeerConnectionService` implementation.

### 3.4 Unit Tests — Application & Apps Test Suites
- Delete tests exclusively testing `DirectP2P`, `IStreamRegistry`, `PortRangePrivacyPreset`, `ConnectToDirectPeerAsync`, or `IPeerStreamBlobClient`.
- Add/update high-value tests:
  - `PeerInvitationCodecTests`: binary round-trip (`percolator://invite/<base64url>`, exact 4-field layout with 32-byte `IdentityKey` and zero `DeliveryToken`); rejects malformed buffer, truncated length, unknown format byte, zeroed keys/tokens, or invalid relay URI.
  - `OutboundConnectionWorkflowTests`:
    - Rejects with `EXCHANGE_NOT_LINKED` when caller has no registered mailbox on the invitation's Exchange.
    - Rejects with `CONTACT_BLOCKED` if peer is blocked and with `IDENTITY_KEY_CHANGED` if existing contact has a different `IdentityKey` (zero network calls, zero DB writes).
    - Aborts on `IDENTITY_KEY_MISMATCH` (MitM relay key swap) or `INVALID_PREKEY_SIGNATURE` before **any** repository writes.
    - Returns `OutboundConnectionStatus.PendingOffline` cleanly when `FetchAuthenticatedAsync` times out or Exchange is unreachable (zero partial DB writes).
    - On valid fetched bundle: saves initial Exchange-scoped routing capability, contact in `PendingOutbound` with pinned `IdentityKey`, Exchange-scoped channel in `PendingAcceptance`, and atomically persists 4-DH session + enqueues authenticated initial PreKeyMessage.
  - `HandshakeServiceTests`:
    - `InitiateHandshakeAsync` performs 4-DH X3DH against verified bundle, pads initial payload (`SYS-PAYLOAD-LENGTH-PADDING`), atomically persists Exchange-scoped session + outbox job under `ChannelId.DeterministicForDirect(ownerId, peerId, relayEndpoint)` with explicit `[appId:1B]` framing, and zeroizes secrets.
    - `CompletePendingHandshakeAsync` runs during `AcceptRequestAsync`, unpads/decrypts initial capability, and stores `PeerMailboxRoutingCapability` (with `DeliveryToken`) prior to `DirectChannel.Activate`.
  - `InboundIngressPipelineTests`: inbound E2EE reply from `PendingOutbound` peer transitions contact to `Active`, dispatches `ContactOutboundAcceptedEvent` with `PeerTrustLevel.Tofu`, and persists inviter's `DeliveryToken` so subsequent egress uses unauthenticated Sealed Sender.
  - `ChatInitialPayloadProviderTests` & `ChatPayloadHandlerTests`: emits sender's Exchange-matched reply capability (including `DeliveryToken`, zero local IP/port); inbound capability updates Exchange-scoped `IPeerMailboxRoutingRepository` and `DirectChannel.RelayEndpoint`.

### 3.5 Build Gate
- `dotnet build Percolator.Domain.Kernel`
- `dotnet test Percolator.Domain.Tests`
- `dotnet test Percolator.Domain.IntegrationTests`
- `dotnet test Percolator.Apps.FileTransfer.Domain.Tests`
- `dotnet test Percolator.Application.Tests`
- `dotnet test Percolator.Apps.Chat.Tests`
- `dotnet test Percolator.Apps.FileTransfer.Tests`
- `dotnet test Percolator.Apps.Discovery.Tests`
- All 8 suites build and pass 100%. (`Percolator.Infrastructure`, `Percolator.Desktop`, `Percolator.Simulator` expected to fail build.)

---

## Step 4: Critical Review & Remediation — Application & Plugin Apps

### 4.1 Audit Checklist
- Zero references to `DirectP2P`, `DirectPeer`, `IStreamRegistry`, `IStreamRegistryManagement`, `IDirectEndpointRegistry`, `PortRangePrivacyPreset`, `IPeerStreamBlobClient`, or `allowDirectEndpoint` in `Percolator.Application` and all `Percolator.Apps.*`.
- Zero nullable optional service dependencies (`= null`) in `HandshakeService`, `PeerConnectionService`, `OutboundConnectionWorkflow`, `InboundConnectionWorkflow`, `OutboundEgressPipeline`, `ChatPayloadHandler`.
- Zero fallback magic constants (`"127.0.0.1"`, `5100`) anywhere in Application or Apps.
- Strict verify-before-commit & re-invitation safety enforced in `OutboundConnectionWorkflow`: `EXCHANGE_NOT_LINKED` / `CONTACT_BLOCKED` / `IDENTITY_KEY_CHANGED` check → `FetchAuthenticatedAsync` → `VerifyFetchedBundle` (pinned `IdentityKey` + Ed25519 SPK signature) MUST pass before any DB write.
- Bucketed payload padding (`SYS-PAYLOAD-LENGTH-PADDING`) and atomic ratchet+outbox transaction (`SYS-ATOMIC-STATE-PERSISTENCE`) enforced in both `HandshakeService` and `OutboundEgressPipeline`.
- Zero `Array.Clear` on secrets/plaintexts (`CryptographicOperations.ZeroMemory` used everywhere per `SYS-DETERMINISTIC-ZEROIZATION`).
- Wire framing of initial handshake payload includes explicit `AppId`; `CompletePendingHandshakeAsync` runs during `AcceptRequestAsync` before `DirectChannel.Activate`.
- `OutboxJob.Create` uses Exchange-scoped `ChannelId.DeterministicForDirect(ownerId, remotePeerId, relayEndpoint)` everywhere for 1:1 envelopes (never `ChannelId.New()`).
- Zero JSON serialization and zero `DeliveryToken` in `PeerInvitationCodec` (`percolator://invite/<base64url>` per `DEC-CANONICAL-URI-SCHEME`).
- Test audit: prune brittle mock-sequence/interaction tests; verify every test asserts observable domain/state/crypto outcomes.
- Re-run all 8 build gate test suites → 100% green before Step 5.

---

## Step 5: Infrastructure Refactor

### 5.1 Deletions
- `Percolator.Infrastructure/Transport/StreamRegistry.cs`
- `Percolator.Infrastructure/Plugins/BlobStorage/PeerStreamBlobClient.cs`
- In `Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs`: delete `UnwrapDirect(...)` (all unwrapping is envelope-mediated via `Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId)`).
- In `Percolator.Application/Ports/IPeerTransportServer.cs` and `Percolator.Infrastructure/Transport/KestrelPeerTransportServer.cs`: delete `bool mapRelayEndpoints = false` parameter from `StartAsync`.

### 5.2 Protobuf Changes
- `Percolator.Infrastructure/Protos/unauthenticated_delivery.proto`:
  - Delete `rpc DeliverDirect (SealedEnvelopeProto) returns (DeliveryAckProto);`
  - Delete `message DeliveryAckProto`.
  - Delete unauthenticated `rpc FetchPreKeyBundle` (moved to `authenticated_relay.proto` under Choice A — Exact Signal Default so first-contact bundle fetches are authenticated and per-member rate-limited).
  - Keep `EnqueueMailbox` (post-approval Sealed Sender with mandatory `DeliveryToken`), `GetProfile` / `UploadProfile` / `DeleteProfile`, and `UploadBlindBlob` / `DownloadBlindBlob`.
- `Percolator.Infrastructure/Protos/authenticated_relay.proto`:
  - Keep existing RPCs (`RegisterMailbox`, `PublishPreKeyBundle`, `ConnectMailboxStream`, `CreateGroupLedger`, `CommitGroupLedger`, `GetGroupLedgerState`, etc.).
  - Extend `RegisterMailboxResponseProto` with `RegistrationStatus` (`APPROVED`, `PENDING_APPROVAL`) to support `RelayRegistrationStrategy.ApprovalRequired`.
  - Add `rpc FetchPreKeyBundle (FetchPreKeyBundleRequestProto) returns (PreKeyBundleProto);` (authenticated member PreKey fetch per Choice A).
  - Add `rpc SendInitialHandshake (SendInitialHandshakeRequestProto) returns (EnqueueMailboxResponseProto);` (authenticated first-contact PreKeyMessage delivery without `DeliveryToken` per Choice A).

### 5.3 Transport & Endpoint Implementation Changes
- **True Per-Recipient Sealed Sender (`Percolator.Infrastructure/Transport/SealedWireFraming.cs` & `SealedEnvelopeUnwrapper.cs`)**:
  - Delete hardcoded `DefaultKeyIkm` completely (`SYS-BLINDED-RELAY-ENVELOPES` / `DEC-SEALED-SENDER-TRANSPORT`).
  - Implement Signal Sealed Sender v1 (`UnidentifiedSenderMessage`) strictly per Signal's audited specification using `SignalCryptoEngine`:
    1. Generate fresh ephemeral Curve25519 key pair $(sk_{\text{eph}}, pk_{\text{eph}})$ per envelope.
    2. Compute $ss_1 = \text{ECDH}(sk_{\text{eph}}, pk_R)$ against the recipient's `IdentityKey` ($pk_R$), derive chain + cipher key via HKDF-SHA256 (`"UnidentifiedDelivery" || pk_R || pk_eph`), and AEAD-encrypt the sender's static public key $pk_S$ → $c_1$.
    3. Compute $ss_2 = \text{ECDH}(sk_S, pk_R)$, advance HKDF-SHA256 with $k_{\text{chain}} || c_1$, and AEAD-encrypt `UnidentifiedSenderMessageContent` (inner sender credential + Ed25519 signature + inner Signal ratchet ciphertext) → $c_2$.
    4. Zeroize all ephemeral secret keys, ECDH shared secrets, and intermediate plaintext buffers via `CryptographicOperations.ZeroMemory` in `finally` blocks (`SYS-DETERMINISTIC-ZEROIZATION`).
  - Recipient unwrapping in `SealedEnvelopeUnwrapper.Unwrap(MailboxEnvelope, PublicIdentityId)` uses only the recipient Persona's private `IdentityKey` from the isolated Persona scope.

- **Per-Persona & Auth/Unauth Segregated Channel Pools (`Percolator.Infrastructure/Transport/PeerGrpcChannelFactory.cs`)**:
  - Scope `PeerGrpcChannelFactory` strictly **per Persona** (register as scoped/per-Persona instance rather than a process-wide singleton so separate Personas never share HTTP/2 connections or TLS sessions — `SYS-ISOLATED-PERSONAS`).
  - Segregate channels into two distinct pools per Persona: **Authenticated** (`AuthenticatedRelayEndpoint`) vs. **Unauthenticated** (`UnauthenticatedDeliveryEndpoint` / `PrivateRendezvousRelayEndpoint`) so unauthenticated Sealed Sender sends never reuse an authenticated HTTP/2 connection (`FACT-SIGNAL-SEALED-SENDER-UNAUTH-CHANNEL`).
  - Retain ephemeral per-startup self-signed TLS certificates (no TLS certificate pinning on disk; unpinned TLS / IP-takeover residual risk is tracked in `RiskRegister.md` under `HAZ-EPHEMERAL-TLS-IP-TAKEOVER`).

- `Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`:
  - Strictly handles **post-approval unauthenticated Sealed Sender** (`EnqueueMailbox`), profiles, and blind blobs.
  - Delete `DeliverDirect` and `FetchPreKeyBundle` overrides (PreKey fetching is authenticated on `AuthenticatedRelayEndpoint`).
  - Simplify constructor: delete the dual-mode constructor and delete `IIngressFilterService`, `ISealedEnvelopeUnwrapper`, `IInboundIngressPipeline`, `IActiveIdentitySet`, and `IRelayPreKeyRepository` fields and constructor arguments.
  - Require exactly one constructor:
    ```csharp
    // Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs
    public sealed class UnauthenticatedDeliveryEndpoint : UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase
    {
        private readonly IRelayProfileRepository _profileRepository;
        private readonly ILocalChunkStore _chunkStore;
        private readonly IRelayMailboxCapabilityStore _capabilityStore;
        private readonly IRelayHostingPolicyProvider _policyProvider;

        public UnauthenticatedDeliveryEndpoint(
            IRelayProfileRepository profileRepository,
            ILocalChunkStore chunkStore,
            IRelayMailboxCapabilityStore capabilityStore,
            IRelayHostingPolicyProvider policyProvider)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
            _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
            _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        }
    ```
  - Enforce `RelayHostingPolicy`, mandatory constant-time `DeliveryToken` verification, and **cumulative per-mailbox byte + envelope-count quotas** on `EnqueueMailbox`:
    ```csharp
    public override async Task<EnqueueMailboxResponseProto> EnqueueMailbox(EnqueueMailboxRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (request.DeliveryToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "DeliveryToken is required for unauthenticated mailbox delivery."));
        }

        if (request.Envelope.EncryptedPackage.Length > policy.MaxMailboxBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Envelope size exceeds relay mailbox quota."));
        }

        var routingToken = BlindedRoutingToken.FromBytes(request.RecipientRoutingToken.ToByteArray());
        var deliveryToken = DeliveryToken.FromBytes(request.DeliveryToken.ToByteArray());
        var envelope = new MailboxEnvelope(
            request.Envelope.EncryptedPackage.ToByteArray(),
            DateTimeOffset.UtcNow.Add(policy.EnvelopeTtl));

        // Enforces constant-time DeliveryToken verification AND cumulative per-mailbox byte + envelope-count quotas
        var result = await _capabilityStore.EnqueueEnvelopeAsync(routingToken, deliveryToken, envelope, policy.MaxMailboxBytes, context.CancellationToken);
        if (result.IsFailure)
        {
            var statusCode = result.Error.Code == "MAILBOX_QUOTA_EXCEEDED"
                ? StatusCode.ResourceExhausted
                : StatusCode.PermissionDenied;
            throw new RpcException(new Status(statusCode, result.Error.Description));
        }

        return new EnqueueMailboxResponseProto { Success = true };
    }
    ```
  - Apply corresponding `policy.IsFeatureEnabled(RelayFeatureFlags.Profiles)` checks in `GetProfile`, `UploadProfile`, and `DeleteProfile`.
  - Apply corresponding `policy.IsFeatureEnabled(RelayFeatureFlags.Blobs)` checks in `DownloadBlindBlob` and `UploadBlindBlob`.
  - In `UploadBlindBlob`: replace `Path.GetTempPath()` staging with a staging subdirectory inside the machine-local application data root (`SYS-LOCAL-STORAGE-CONTAINMENT` / `DEP-INFRA-LOCAL-STORAGE-CONTAINMENT`).

- `Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs`:
  - Inject non-nullable `IRelayHostingPolicyProvider`, `ILocalRelayAssociationRepository`, `IPendingMailboxRegistrationRepository`, `IRelayPreKeyRepository`, `IRelayMailboxCapabilityStore`, and `IAuthenticatedMemberRateLimiter` (replace hardcoded `RelayHostingPolicy.Default`).
  - **Operator-Controlled Registration Strategy + `AutoApproveLocalhost` Fast Path** (preserves real localhost TLS/gRPC traversal with zero network bypasses):
    - Before a local Persona registers with a local Self-Hosted Exchange over localhost, the local Persona writes a `LocalRelayAssociation` record (its authorized `BlindedRoutingToken`) to the local DB prior to dialing localhost TLS/gRPC.
    - At the gRPC edge of `RegisterMailbox`:
      1. If `policy.AutoApproveLocalhost` is `true`, `IPAddress.IsLoopback(remoteIp)` is `true`, and `await _localAssociationRepo.IsAuthorizedAsync(routingToken, ct)` is `true` → auto-approve immediately and provision the mailbox up to `policy.MaxMailboxes`.
      2. Else if `policy.RegistrationStrategy == RelayRegistrationStrategy.Closed` → **silently hang up** (`context.GetHttpContext().Abort()` — emit zero gRPC status or error text).
      3. Else if the applicant's `BlindedRoutingToken` is marked `Rejected` or `Blocked` in `IPendingMailboxRegistrationRepository` → **silently hang up** (`context.GetHttpContext().Abort()`).
      4. Else if `policy.RegistrationStrategy == RelayRegistrationStrategy.ApprovalRequired` → persist applicant into `PendingMailboxRegistrations` (`Status = PendingApproval`) and return `RegistrationStatus.PENDING_APPROVAL` without allocating a mailbox until the Exchange host explicitly approves via `IExchangeRegistrationModerationService` in the Exchange Control UI.
      5. Else (`RelayRegistrationStrategy.Open`) → approve immediately up to `policy.MaxMailboxes`.
  - **Authenticated `FetchPreKeyBundle` (Choice A — Exact Signal Default + Per-Member Rate Limiting)**:
    - Authenticate caller via valid Exchange membership credentials (caller's registered `BlindedRoutingToken` + `DrainToken`).
    - Reject if `!policy.IsFeatureEnabled(RelayFeatureFlags.PreKeys)` (`StatusCode.PermissionDenied`).
    - Enforce per-member authenticated sliding-window rate limit (`IAuthenticatedMemberRateLimiter.TryConsumeAsync(callerRoutingToken, "FetchPreKeyBundle", ct)`); reject excess requests with `StatusCode.ResourceExhausted` (preventing OTPK exhaustion DoS without storing cross-peer association graphs — residual first-contact visibility tracked under `HAZ-FIRST-CONTACT-ASSOCIATION`).
    - Atomically dequeue 1 OTPK via `_preKeyRepository.ConsumePreKeyBundleAsync(targetRoutingToken, deviceId, ct)` (3-DH fallback only when pool is empty).
  - **Authenticated `SendInitialHandshake` (Choice A — First-Contact PreKeyMessage Delivery)**:
    - Authenticate caller via valid Exchange membership credentials (no `DeliveryToken` required for the single first-contact PreKeyMessage).
    - Enforce per-member authenticated sliding-window rate limit (`IAuthenticatedMemberRateLimiter.TryConsumeAsync(callerRoutingToken, "SendInitialHandshake", ct)`) and cumulative recipient `MaxMailboxBytes` + envelope-count quotas.
    - Enqueue the sealed initial PreKeyMessage into the recipient's mailbox (`_capabilityStore.EnqueueAuthenticatedInitialAsync(...)`). Once the inviter approves and replies E2EE with `DeliveryToken`, all subsequent messages use unauthenticated Sealed Sender on `UnauthenticatedDeliveryEndpoint.EnqueueMailbox`.
  - `PublishPreKeyBundle`: reject if `!policy.IsFeatureEnabled(RelayFeatureFlags.PreKeys)`. Store plaintext public PreKeyBundle (`IdentityKey`, `SignedPreKey`, `SignedPreKeySignature`, normalized `OneTimePreKeys`) matching Signal's specification.
  - Deterministically zeroize all copied `DrainToken` byte buffers (`drainToken.Span.ToArray()`) in `finally` blocks using `CryptographicOperations.ZeroMemory` (`SYS-DETERMINISTIC-ZEROIZATION`).

- `Percolator.Infrastructure/Transport/TransportDispatcher.cs`:
  - Remove `DeliverDirectAsync` from `IGrpcDeliveryInvoker` and `GrpcDeliveryInvoker`.
  - Remove `IStreamRegistryManagement` constructor parameter and field.
  - Delete `DeliveryRouteType.DirectP2P` switch arm; dispatch based on Choice A routing semantics and segregated channel pools:
    - `DeliveryRouteType.RelayedOneToOne` with `route.TargetDeliveryToken == null` (first-contact PreKeyMessage): dispatch via `AuthenticatedRelayServiceClient.SendInitialHandshakeAsync` over the Persona's **authenticated** HTTP/2 channel pool.
    - `DeliveryRouteType.RelayedOneToOne` with `route.TargetDeliveryToken != null` (post-approval 1:1 messages) and `DeliveryRouteType.RelayedGroup`: dispatch via `UnauthenticatedDeliveryServiceClient.EnqueueMailboxAsync` over the Persona's **unauthenticated** HTTP/2 channel pool:
    ```csharp
    case DeliveryRouteType.RelayedOneToOne when route.TargetDeliveryToken is null:
        var initialRequest = new SendInitialHandshakeRequestProto
        {
            RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.Span),
            Envelope = envelopeProto
        };
        await _invoker.SendInitialHandshakeAuthenticatedAsync(ownerId, route.RelayEndpoint.Uri, initialRequest, ct).ConfigureAwait(false);
        break;

    case DeliveryRouteType.RelayedOneToOne:
    case DeliveryRouteType.RelayedGroup:
        var request = new EnqueueMailboxRequestProto
        {
            RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.Span),
            DeliveryToken = ByteString.CopyFrom(route.TargetDeliveryToken!.Value.Span),
            Envelope = envelopeProto
        };
        await _invoker.EnqueueMailboxUnauthenticatedAsync(route.RelayEndpoint.Uri, request, ct).ConfigureAwait(false);
        break;
    ```

- `Percolator.Infrastructure/Transport/KestrelPeerTransportServer.cs`:
  - Update `StartAsync` signature:
    ```csharp
    public async Task StartAsync(
        IPAddress? bindAddress = null,
        PortNumber? preferredPort = null,
        CancellationToken ct = default)
    ```
  - Register and map all 3 unified relay endpoints unconditionally:
    ```csharp
    builder.Services.AddGrpc();
    builder.Services.AddSingleton<IGrpcServiceActivator<UnauthenticatedDeliveryEndpoint>>(
        new ParentContainerServiceActivator<UnauthenticatedDeliveryEndpoint>(_serviceProvider));
    builder.Services.AddSingleton<IGrpcServiceActivator<AuthenticatedRelayEndpoint>>(
        new ParentContainerServiceActivator<AuthenticatedRelayEndpoint>(_serviceProvider));
    builder.Services.AddSingleton<IGrpcServiceActivator<PrivateRendezvousRelayEndpoint>>(
        new ParentContainerServiceActivator<PrivateRendezvousRelayEndpoint>(_serviceProvider));

    app = builder.Build();
    app.MapGrpcService<UnauthenticatedDeliveryEndpoint>();
    app.MapGrpcService<AuthenticatedRelayEndpoint>();
    app.MapGrpcService<PrivateRendezvousRelayEndpoint>();
    ```

- `Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs` & `Percolator.Infrastructure/Transport/DiscoveryQueryClient.cs`:
  - Make `IPeerMailboxRoutingRepository` a **non-nullable required** constructor dependency (remove `?` and `= null`).
  - In `PreKeyBundleProviderAdapter.cs`, implement `FetchAuthenticatedAsync(PublicIdentityId callerOwnerId, RelayEndpoint relayEndpoint, BlindedRoutingToken targetRoutingToken, DeviceId? deviceId, CancellationToken ct)` invoking `AuthenticatedRelayServiceClient.FetchPreKeyBundleAsync` over `callerOwnerId`'s **authenticated** HTTP/2 channel pool (attaching caller's Exchange mailbox credentials, zero routing repository lookup prior to bundle verification) and mapping the protobuf response to `DomainResult<PreKeyBundle>`.

### 5.4 Persistence — Zero-JSON Normalized Schema & Atomic OTPK Dequeue (Clean Baseline, Zero Migration Scripts)
- Because zero deployed clients exist, update the baseline target schema directly in `PercolatorDbContext` and entity definitions (no `ALTER TABLE` migration scripts).
- `Percolator.Infrastructure/Persistence/Entities/OpsEntities.cs`:
  - Replace `string? DeliveryRouteJson` on `OpsOutboxJobEntity` with typed relational columns:
    ```csharp
    // Percolator.Infrastructure/Persistence/Entities/OpsEntities.cs
    public sealed class OpsOutboxJobEntity
    {
        public long SequenceNumber { get; set; }
        public Guid Id { get; set; }
        public Guid OwnerIdentityId { get; set; }
        public Guid ChannelId { get; set; }
        public Guid? RecipientId { get; set; }
        public int DeliveryRouteType { get; set; }
        public string TargetRelayEndpoint { get; set; } = string.Empty;
        public byte[] TargetRoutingToken { get; set; } = Array.Empty<byte>();
        public byte[]? TargetDeliveryToken { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public int Status { get; set; }
        public int RetryCount { get; set; }
        public int MaxAttempts { get; set; }
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string? LastError { get; set; }
    }
    ```
- **Normalized Relay PreKeys, Exchange Moderation, Local-Association & Option 3A Multi-Exchange Entities**:
  - Replace `string OneTimePreKeysJson` on `RelayPreKeyBundleEntity` with a normalized relational child table `RelayOneTimePreKeys` (`OwnerRoutingToken`, `DeviceId`, `PreKeyId`, `PublicKey` — composite primary key `(OwnerRoutingToken, DeviceId, PreKeyId)`). Delete `System.Text.Json` usage from `SqliteRelayPreKeyRepository` completely (`DEP-INFRA-PREKEY-AVAILABILITY`).
  - Implement atomic `ConsumePreKeyBundleAsync(BlindedRoutingToken ownerRoutingToken, DeviceId deviceId, CancellationToken ct)` in `SqliteRelayPreKeyRepository`:
    - Synchronizes concurrent fetches per `(ownerRoutingToken, deviceId)` via an async lock + SQLite `BEGIN IMMEDIATE` transaction that reads the bundle header (`IdentityKey`, `SignedPreKey`, `SignedPreKeySignature`), pops and deletes exactly 1 row from `RelayOneTimePreKeys` (`ORDER BY PreKeyId ASC LIMIT 1`), and commits atomically (returns `OneTimePreKey = null` for 3-DH fallback only when 0 rows exist).
  - Add `PendingMailboxRegistrationEntity` (`PendingMailboxRegistrations` table: `RelayId`, `ApplicantRoutingToken`, `Status` [`PendingApproval`, `Approved`, `Rejected`, `Blocked`], `RequestedAtUtc`, `DecidedAtUtc`) + `IPendingMailboxRegistrationRepository` / `SqlitePendingMailboxRegistrationRepository` and `ExchangeRegistrationModerationService` (`IExchangeRegistrationModerationService`) backing `RelayRegistrationStrategy.ApprovalRequired`.
  - Add `LocalRelayAssociationEntity` (`LocalRelayAssociations` table: `RelayId`, `AuthorizedRoutingToken`, `CreatedAtUtc`) + `ILocalRelayAssociationRepository` / `SqliteLocalRelayAssociationRepository` for `RelayHostingPolicy.AutoApproveLocalhost` loopback pre-association checks.
  - Scope `PeerMailboxRoutings` composite key to `(OwnerIdentityId, PeerIdentityId, RelayEndpoint)` and scope `DirectChannels` + `RatchetSessions` to Exchange-scoped `ChannelId.DeterministicForDirect(ownerId, peerId, relayEndpoint)` (Option 3A: independent channels and ratchets per shared Exchange).

- `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`:
  - Configure `OpsOutboxJobs`, `RelayPreKeyBundles`, `RelayOneTimePreKeys`, `PendingMailboxRegistrations`, `LocalRelayAssociations`, and Exchange-scoped `PeerMailboxRoutings` directly in the baseline EF Core model:
    ```csharp
    modelBuilder.Entity<OpsOutboxJobEntity>(entity =>
    {
        entity.ToTable("OpsOutboxJobs");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TargetRelayEndpoint).IsRequired().HasMaxLength(2048);
        entity.Property(e => e.TargetRoutingToken).IsRequired();
        entity.Property(e => e.TargetDeliveryToken).IsRequired(false);
    });

    modelBuilder.Entity<RelayOneTimePreKeyEntity>(entity =>
    {
        entity.ToTable("RelayOneTimePreKeys");
        entity.HasKey(e => new { e.OwnerRoutingToken, e.DeviceId, e.PreKeyId });
        entity.Property(e => e.PublicKey).IsRequired();
    });

    modelBuilder.Entity<PendingMailboxRegistrationEntity>(entity =>
    {
        entity.ToTable("PendingMailboxRegistrations");
        entity.HasKey(e => new { e.RelayId, e.ApplicantRoutingToken });
    });

    modelBuilder.Entity<LocalRelayAssociationEntity>(entity =>
    {
        entity.ToTable("LocalRelayAssociations");
        entity.HasKey(e => new { e.RelayId, e.AuthorizedRoutingToken });
    });
    ```
  - Retain `PortRangeStart` and `PortRangeEnd` on `VaultSettingsEntities` to persist local host Self-Hosted Exchange binding preferences.

- `Percolator.Infrastructure/Persistence/Repositories/SqliteOutboxRepository.cs`:
  - Delete `System.Text.Json` import and `DeliveryRouteDto` class completely.
  - In `ToEntity`:
    ```csharp
    TargetRelayEndpoint = job.Route.RelayEndpoint.ToString(),
    TargetRoutingToken = job.Route.TargetToken.Span.ToArray(),
    TargetDeliveryToken = job.Route.TargetDeliveryToken?.Span.ToArray()
    ```
  - In `ToDomain`:
    ```csharp
    var route = (DeliveryRouteType)entity.DeliveryRouteType switch
    {
        DeliveryRouteType.RelayedOneToOne => new DeliveryRoute(
            DeliveryRouteType.RelayedOneToOne,
            BlindedRoutingToken.FromBytes(entity.TargetRoutingToken),
            RelayEndpoint.Parse(entity.TargetRelayEndpoint),
            entity.TargetDeliveryToken != null ? DeliveryToken.FromBytes(entity.TargetDeliveryToken) : null),
        DeliveryRouteType.RelayedGroup => new DeliveryRoute(
            DeliveryRouteType.RelayedGroup,
            BlindedRoutingToken.FromBytes(entity.TargetRoutingToken),
            RelayEndpoint.Parse(entity.TargetRelayEndpoint),
            entity.TargetDeliveryToken != null ? DeliveryToken.FromBytes(entity.TargetDeliveryToken) : null),
        _ => throw new InvalidOperationException($"Unsupported persistent delivery route type: {entity.DeliveryRouteType}")
    };
    ```

- `Percolator.Infrastructure/Persistence/Repositories/SqlChatMessageRepository.cs`:
  - Line 366: replace fallback `(int)BlobStorageMode.DirectP2P` with `(int)BlobStorageMode.RelayHosted`.

- `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` & `Percolator.Infrastructure/Persistence/BaselineStage2HostFactory.cs`:
  - Remove `StreamRegistry`, `IStreamRegistry`, `IStreamRegistryManagement`, `IDirectEndpointRegistry`, `PeerStreamBlobClient`, `IPeerStreamBlobClient` registrations.
  - Register `PeerGrpcChannelFactory` strictly scoped per Persona (never shared across Personas).
  - Register `IRelayHostingPolicyProvider`, `ILocalRelayAssociationRepository`, `IPendingMailboxRegistrationRepository`, `IExchangeRegistrationModerationService`, and `IAuthenticatedMemberRateLimiter`.

### 5.4b Infrastructure.Windows Modifications & Tests
- `Percolator.Infrastructure.Windows.Tests/Transport/WindowsPeerGrpcTofuRoundTripTests.cs`:
  - Update round-trip test from `client.DeliverDirectAsync(request)` to `client.EnqueueMailboxAsync(request)`:
    ```csharp
    var request = new EnqueueMailboxRequestProto
    {
        RecipientRoutingToken = ByteString.CopyFrom(new byte[16]),
        DeliveryToken = ByteString.CopyFrom(new byte[32]),
        Envelope = new SealedEnvelopeProto
        {
            EncryptedPackage = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 })
        }
    };

    var response = await client.EnqueueMailboxAsync(request);
    response.Should().NotBeNull();
    response.Success.Should().BeTrue();
    ```
  - Remove stub registrations for `IIngressFilterService`, `ISealedEnvelopeUnwrapper`, `IInboundIngressPipeline`, and `IActiveIdentitySet` (reflecting simplified `UnauthenticatedDeliveryEndpoint`).
  - Register `IRelayHostingPolicyProvider` returning `RelayHostingPolicy.Default`.
  - Verify Windows SChannel TLS 1.3 negotiation succeeds against `EnqueueMailbox`.

### 5.5 Unit Tests — `Percolator.Infrastructure.Tests` & `Percolator.Infrastructure.Windows.Tests`
- Delete `StreamRegistryTests`, `DeliverDirect` endpoint tests, and `PeerStreamBlobClientTests`.
- Add/update tests in `Percolator.Infrastructure.Tests`:
  - `SealedWireFramingTests`: round-trip Signal Sealed Sender v1 (`UnidentifiedSenderMessage`) with real `SignalCryptoEngine`; assert envelope sealed to Bob cannot be unwrapped by Carol or by an Exchange without Bob's private `IdentityKey`; verify zero hardcoded static keys.
  - `PeerGrpcChannelFactoryTests`: verify separate Personas receive isolated channel pools and authenticated vs. unauthenticated channels are never shared.
  - `SqliteOutboxRepositoryTests`: round-trip `DeliveryRoute` via relational columns (`TargetRelayEndpoint`, `TargetRoutingToken`, `TargetDeliveryToken` null & non-null) with real in-memory SQLite; verify invalid/missing relay endpoint throws/fails cleanly.
  - `SqliteRelayPreKeyRepositoryTests`: concurrent `ConsumePreKeyBundleAsync` tasks (`Task.WhenAll`) against real SQLite dispense strictly distinct OTPKs with zero duplicates and zero JSON storage.
  - `UnauthenticatedDeliveryEndpointTests`: rejects `EnqueueMailbox` when `DeliveryToken` is empty or invalid (constant-time failure); enforces cumulative `MaxMailboxBytes` and caps TTL to `EnvelopeTtl`; policy gate rejects `GetProfile`/`UploadProfile` and `UploadBlindBlob` when respective `RelayFeatureFlags` bit is unset; `UploadBlindBlob` stages strictly under machine-local app data root (never `Path.GetTempPath()`).
  - `AuthenticatedRelayEndpointTests`:
    - Under `RelayRegistrationStrategy.ApprovalRequired` + `AutoApproveLocalhost = true`: remote callers stage into `PendingMailboxRegistrations` (`PENDING_APPROVAL`) until operator approves (and rejected/blocked callers trigger silent `HttpContext.Abort()`), while pre-associated local Personas over loopback auto-approve over real localhost TLS/gRPC.
    - Under `RelayRegistrationStrategy.Closed`: non-auto-approved callers trigger silent connection abort (`HttpContext.Abort()`) with zero gRPC status response while existing mailboxes remain active.
    - Authenticated `FetchPreKeyBundle` and `SendInitialHandshake` require valid member credentials, enforce `RelayFeatureFlags.PreKeys`, enforce per-member sliding-window rate limits (`StatusCode.ResourceExhausted`), and atomically dequeue OTPKs.
    - Enforces `MaxMailboxes` and zeroizes copied `DrainToken` buffers.
  - `TransportDispatcherTests`: dispatches `RelayedOneToOne` with `TargetDeliveryToken == null` via `SendInitialHandshakeAuthenticatedAsync` on the **authenticated** channel pool, and dispatches `RelayedOneToOne` (non-null `TargetDeliveryToken`) and `RelayedGroup` via `EnqueueMailboxUnauthenticatedAsync` on the **unauthenticated** channel pool.
- Run `Percolator.Infrastructure.Windows.Tests` (33 tests pass 100%).

### 5.6 Build Gate
- `dotnet build Percolator.Domain.Kernel`
- `dotnet test Percolator.Domain.Tests`
- `dotnet test Percolator.Domain.IntegrationTests`
- `dotnet test Percolator.Apps.FileTransfer.Domain.Tests`
- `dotnet test Percolator.Application.Tests`
- `dotnet test Percolator.Apps.Chat.Tests`
- `dotnet test Percolator.Apps.FileTransfer.Tests`
- `dotnet test Percolator.Apps.Discovery.Tests`
- `dotnet test Percolator.Infrastructure.Tests`
- `dotnet test Percolator.Infrastructure.Windows.Tests`
- All 10 suites build and pass 100%. (`Percolator.Desktop`, `Percolator.Simulator` expected to fail build.)

---

## Step 6: Critical Review & Remediation — Infrastructure & Layer Boundary Audit

### 6.1 Audit Checklist
- Boundary scan:
  - `git grep "Percolator.Infrastructure" -- Percolator.Domain Percolator.Domain.Kernel Percolator.Domain.IntegrationTests Percolator.PluginSdk Percolator.Application Percolator.Apps.*` → 0 matches.
  - `git grep "Google.Protobuf\|Microsoft.Data.Sqlite\|Microsoft.EntityFrameworkCore\|Grpc\." -- Percolator.Domain Percolator.Domain.Kernel Percolator.PluginSdk Percolator.Application` → 0 matches.
  - `git grep "System.Text.Json\|Newtonsoft.Json\|JsonSerializer\|OneTimePreKeysJson\|DeliveryRouteJson" -- Percolator.Domain Percolator.PluginSdk Percolator.Application Percolator.Infrastructure Percolator.Infrastructure.Windows Percolator.Apps.*` → 0 matches.
  - `git grep "DirectP2P\|DirectPeer\|DeliverDirect\|IStreamRegistry\|IDirectEndpointRegistry\|UnwrapDirect\|DefaultKeyIkm" -- Percolator.Domain Percolator.PluginSdk Percolator.Application Percolator.Infrastructure Percolator.Infrastructure.Windows Percolator.Apps.*` → 0 matches.
  - `git grep "Path.GetTempPath" -- Percolator.Infrastructure` → 0 matches (`SYS-LOCAL-STORAGE-CONTAINMENT`).
- Security & transport invariants:
  - Verify `SealedWireFraming` uses true per-recipient Signal Sealed Sender v1 (ephemeral + static Curve25519 ECDH against recipient `IdentityKey`) with `CryptographicOperations.ZeroMemory` cleanup.
  - Verify `PeerGrpcChannelFactory` is strictly scoped per Persona and segregates authenticated vs. unauthenticated HTTP/2 channels.
  - Verify Choice A split: `FetchPreKeyBundle` and initial PreKeyMessage (`SendInitialHandshake`) run authenticated on `AuthenticatedRelayEndpoint` with per-member sliding-window rate limits; all post-approval messages run unauthenticated Sealed Sender on `UnauthenticatedDeliveryEndpoint.EnqueueMailbox` with mandatory constant-time `DeliveryToken` verification and cumulative mailbox quotas.
  - Verify `AuthenticatedRelayEndpoint` enforces `RelayRegistrationStrategy` (`ApprovalRequired` → `PendingMailboxRegistrations`, `Open` → auto-approve up to `MaxMailboxes`, `Closed` / rejected / blocked → silent `HttpContext.Abort()`) and `AutoApproveLocalhost` loopback + `LocalRelayAssociations` verification without breaking real localhost network traversal.
  - Verify `SqliteRelayPreKeyRepository.ConsumePreKeyBundleAsync` atomically dequeues OTPKs in normalized `RelayOneTimePreKeys` with zero duplicate dispensing under concurrency.
  - Verify Option 3A persistence keys `PeerMailboxRoutings`, `DirectChannels`, and `RatchetSessions` per Exchange (`(OwnerIdentityId, PeerIdentityId, RelayEndpoint)` / Exchange-scoped `ChannelId`).
  - Verify `AuthenticatedRelayEndpoint` zeroizes copied `DrainToken` buffers.
- Interface alignment:
  - Verify `IPeerTransportServer.StartAsync` in `Percolator.Application/Ports/IPeerTransportServer.cs` matches `KestrelPeerTransportServer.StartAsync` (zero `bool mapRelayEndpoints` parameter).
  - Verify `UnauthenticatedDeliveryEndpoint` has exactly one constructor and does NOT depend on unwrapping, filtering, active identity sets, or PreKey repositories.
- Verify zero nullable required DI parameters in `UnauthenticatedDeliveryEndpoint`, `AuthenticatedRelayEndpoint`, `TransportDispatcher`, `PreKeyBundleProviderAdapter`, `DiscoveryQueryClient`.
- Verify zero silent fallbacks (e.g., `?? "127.0.0.1"`, `?? BlobStorageMode.DirectP2P`).
- Verify all DI registrations in `InfrastructureServiceCollectionExtensions` and `BaselineStage2HostFactory` supply all required dependencies cleanly.
- Prune low-value/brittle Infrastructure tests.
- Re-run all 10 test suites → 100% green before Step 7.

---

## Step 7: `Percolator.ApplicationIntegrationTests`

### 7.1 Project Configuration & Legacy Double Clean-up
- `Percolator.ApplicationIntegrationTests/Percolator.ApplicationIntegrationTests.csproj`:
  - Change `<RootNamespace>Percolator.Application2IntegrationTests</RootNamespace>` → `<RootNamespace>Percolator.ApplicationIntegrationTests</RootNamespace>`.
- Delete the 17 obsolete fake/adapter/legacy test files that bypass real transport, use fake crypto, or test deleted Direct P2P code paths:
  - `DirectChatScenarioTests.cs`
  - `Adapters/LoopbackStreamRegistry.cs`
  - `Adapters/LoopbackTransportDispatcher.cs`
  - `Adapters/SealedEnvelopeUnwrapperDirectDouble.cs`
  - `Adapters/InMemoryRepositories.cs`
  - `Adapters/InMemoryDiscoveryDoubles.cs`
  - `Adapters/LoopbackNearbyDiscoveryAgent.cs`
  - `Adapters/LoopbackProfileDeliveryClient.cs`
  - `Adapters/LoopbackRelayDirectory.cs`
  - `Infrastructure/ScenarioCryptoEngine.cs`
  - `Infrastructure/ScenarioBlobCryptoEngine.cs`
  - `Infrastructure/ScenarioZkProofEngine.cs`
  - `Infrastructure/InMemoryLoopbackNetworkSwitch.cs`
  - `Infrastructure/EndpointPort.cs`
  - `Builders/TestIdentityNode.cs`, `Builders/RelayNode.cs`, `Builders/TestClusterBuilder.cs`
  - `Fakes/FakePayloadSender.cs`
  - Legacy `RelayedChatScenarioTests.cs` and `GroupChatScenarioTests.cs` (superseded by new unified suites in 7.3).

### 7.2 Shared Integration Test Harness (`Percolator.ApplicationIntegrationTests/Harness/`)

#### A. `TestRelayServer` (`Harness/TestRelayServer.cs`)
- Runs real `KestrelPeerTransportServer` bound to `IPAddress.Loopback`, port `0` (OS kernel assigns ephemeral port).
- Maintains a persistent keeper SQLite in-memory connection so schemas survive between DI scopes:
  ```csharp
  // Percolator.ApplicationIntegrationTests/Harness/TestRelayServer.cs
  public sealed class TestRelayServer : IAsyncDisposable
  {
      private readonly SqliteConnection _keeperConnection;
      private readonly ServiceProvider _serviceProvider;
      private readonly KestrelPeerTransportServer _server;
      private readonly RelayHostingPolicyHolder _policyHolder;

      public RelayEndpoint Endpoint { get; private set; } = null!;
      public RelayHostingPolicy Policy
      {
          get => _policyHolder.Policy;
          set => _policyHolder.Policy = value;
      }

      public static async Task<TestRelayServer> StartAsync(RelayHostingPolicy? initialPolicy = null)
      {
          var connectionString = $"DataSource=file:relay_{Guid.NewGuid():N}?mode=memory&cache=shared";
          var keeperConnection = new SqliteConnection(connectionString);
          await keeperConnection.OpenAsync();

          await SqliteDatabaseMigrator.MigrateAsync(connectionString);

          var policyHolder = new RelayHostingPolicyHolder(initialPolicy ?? RelayHostingPolicy.Default);

          var services = new ServiceCollection();
          services.AddLogging();
          services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
          services.AddSingleton<IRelayHostingPolicyProvider>(policyHolder);
          // Register SQLite repositories, chunk store, and endpoints
          services.AddSingleton<UnauthenticatedDeliveryEndpoint>();
          services.AddSingleton<AuthenticatedRelayEndpoint>();
          services.AddSingleton<PrivateRendezvousRelayEndpoint>();

          var serviceProvider = services.BuildServiceProvider();
          var certProvider = serviceProvider.GetRequiredService<ITransportCertificateProvider>();
          var server = new KestrelPeerTransportServer(certProvider, serviceProvider, new PeerTransportServerOptions());

          await server.StartAsync(IPAddress.Loopback, new PortNumber(0));
          var boundPort = server.BoundPort!.Value.Value;

          return new TestRelayServer(keeperConnection, serviceProvider, server, policyHolder)
          {
              Endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", boundPort)
          };
      }

      public async Task StopAsync() => await _server.StopAsync();
      public async Task RestartAsync() => await _server.StartAsync(IPAddress.Loopback, new PortNumber(Endpoint.Port));

      public async ValueTask DisposeAsync()
      {
          await _server.DisposeAsync();
          _serviceProvider.Dispose();
          await _keeperConnection.DisposeAsync();
      }
  }
  ```

#### B. `TestPersonaNode` (`Harness/TestPersonaNode.cs`)
- Runs a complete persona node with real `SignalCryptoEngine(new CryptographyOptions())`, isolated keeper `SqliteConnection`, real `TransportDispatcher`, and background hosts (`RelayMailboxDrainHost` and `OutboxDispatchHost`).
- Provides deterministic, event-driven async helpers (zero `Task.Delay` or `Thread.Sleep` polling):
  ```csharp
  // Percolator.ApplicationIntegrationTests/Harness/TestPersonaNode.cs
  public sealed class TestPersonaNode : IAsyncDisposable
  {
      public PublicIdentityId IdentityId { get; }
      public DeviceId DeviceId { get; } = DeviceId.Primary;
      public IServiceProvider Services { get; }

      public async Task LinkRelayAsync(TestRelayServer relay)
      {
          var relayClient = Services.GetRequiredService<IRelayRegistrationClient>();
          var regResult = await relayClient.RegisterMailboxAsync(relay.Endpoint, IdentityId, DeviceId);
          // Persist mailbox capability, fire MailboxBindingChangedEvent to wake DrainScheduler,
          // and publish SPK + 100 OTPKs to the Exchange via PreKeyMaintenanceService.EvaluateAsync(..., forced: true)
      }

      public async Task<string> CreateInvitationAsync(TestRelayServer relay)
      {
          var invService = Services.GetRequiredService<IPeerInvitationService>();
          var invResult = await invService.CreatePeerInvitationAsync(IdentityId, relay.Endpoint);
          if (invResult.IsFailure) throw new InvalidOperationException(invResult.Error.Description);
          return PeerInvitationCodec.Encode(PeerInvitationPayload.FromDomain(invResult.Value));
      }

      public async Task<ChannelId> ConnectWithInvitationAsync(string invitationUri)
      {
          var workflow = Services.GetRequiredService<IOutboundConnectionWorkflow>();
          var payload = PeerInvitationCodec.Decode(invitationUri);
          var result = await workflow.ConnectAsync(IdentityId, payload, TimeSpan.FromSeconds(10));
          return ChannelId.DeterministicForDirect(IdentityId, result.Value.RemotePeerId, payload.RelayEndpoint);
      }

      public async Task ApproveContactAsync(PublicIdentityId peerId)
      {
          var contactRepo = Services.GetRequiredService<IContactRepository>();
          var contact = await contactRepo.GetByIdAsync(IdentityId, peerId);
          contact!.Approve(Services.GetRequiredService<IDateTimeProvider>());
          await contactRepo.SaveAsync(contact);
      }

      public async Task SendChatAsync(ChannelId channelId, string text)
      {
          var chatService = Services.GetRequiredService<IChatService>();
          await chatService.SendMessageAsync(channelId, IdentityId, text);
      }

      public async Task<ChatMessageReceivedEvent> WaitForMessageAsync(ChannelId channelId, string expectedText, TimeSpan timeout)
      {
          var tcs = new TaskCompletionSource<ChatMessageReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
          using var cts = new CancellationTokenSource(timeout);
          cts.Token.Register(() => tcs.TrySetCanceled());

          var subscriber = Services.GetRequiredService<IEventSubscriber>();
          using var sub = subscriber.Subscribe<ChatMessageReceivedEvent>((evt, ct) =>
          {
              if (evt.ChannelId == channelId && evt.Plaintext == expectedText)
              {
                  tcs.TrySetResult(evt);
              }
              return ValueTask.CompletedTask;
          });

          return await tcs.Task;
      }

      public async Task<DirectChannelStatus> WaitForChannelStatusAsync(ChannelId channelId, DirectChannelStatus targetStatus, TimeSpan timeout)
      {
          var tcs = new TaskCompletionSource<DirectChannelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
          using var cts = new CancellationTokenSource(timeout);
          cts.Token.Register(() => tcs.TrySetCanceled());

          var subscriber = Services.GetRequiredService<IEventSubscriber>();
          using var sub = subscriber.Subscribe<DirectChannelStateChangedEvent>((evt, ct) =>
          {
              if (evt.ChannelId == channelId && evt.Status == targetStatus)
              {
                  tcs.TrySetResult(evt.Status);
              }
              return ValueTask.CompletedTask;
          });

          // Check if already in target status
          var channelRepo = Services.GetRequiredService<IDirectChannelRepository>();
          var current = await channelRepo.GetByIdAsync(channelId);
          if (current != null && current.Status == targetStatus)
          {
              return current.Status;
          }

          return await tcs.Task;
      }

      public async Task StopDrainAsync() => await Services.GetRequiredService<RelayMailboxDrainHost>().StopAsync(CancellationToken.None);
      public async Task ResumeDrainAsync() => await Services.GetRequiredService<RelayMailboxDrainHost>().StartAsync(CancellationToken.None);
  }
  ```

#### C. `TestNetworkTopology` (`Harness/TestNetworkTopology.cs`)
- Container managing the lifecycle of `N` relays and `M` personas.
- Exposes `PairPersonasAsync(TestPersonaNode inviter, TestPersonaNode invitee)` encapsulating invitation export, authenticated 1-RTT `FetchPreKeyBundle` + pinned `IdentityKey` verification, authenticated 4-DH PreKeyMessage delivery via shared Exchange, contact approval (exchanging `DeliveryToken` inside E2EE reply), and channel activation confirmation.

### 7.3 Required Integration Test Scenarios (`Percolator.ApplicationIntegrationTests/Scenarios/`)

1. **Single-Relay 1:1 Pinned-Identity PreKey Handshake + Choice A Auth→Sealed-Sender Transition + Symmetric Ratchet (`SingleRelayOneToOneIntegrationTests`)**:
   - Start Relay R1. Provision Personas Alice and Bob on R1 (register mailboxes, publish SPK + 100 OTPKs via `PreKeyMaintenanceService`, start `RelayMailboxDrainHost` and `OutboxDispatchHost`).
   - Bob exports a 4-field `PeerInvitation` (zero `DeliveryToken`) pointing to R1 with pinned `IdentityKey`; Alice decodes and calls `ConnectWithInvitationAsync`.
   - Alice fetches a consumable `PreKeyBundle` from R1 via authenticated `AuthenticatedRelayEndpoint.FetchPreKeyBundle` over Alice's **authenticated** channel pool, verifies pinned `IdentityKey` + Ed25519 SPK signature, executes 4-DH X3DH, and delivers the initial PreKeyMessage via authenticated `AuthenticatedRelayEndpoint.SendInitialHandshake` (Choice A).
   - Bob's `RelayMailboxDrainHost` drains from R1, persists to spool, acks to R1, and processes.
   - Bob approves contact (`PendingApproval` → `Active`) and replies E2EE via R1 using unauthenticated Sealed Sender (carrying Bob's `DeliveryToken` inside `ProfileCapabilityDto`).
   - Alice receives Bob's reply via R1 (`PendingOutbound` → `Active`, `ContactOutboundAcceptedEvent` fired) and persists Bob's `DeliveryToken`.
   - **Symmetric Ratchet & Unauthenticated Sealed Sender Verification**:
     - Alice sends "Message 1" → dispatched via `UnauthenticatedDeliveryEndpoint.EnqueueMailbox` over Alice's **unauthenticated** channel pool → Bob drains and decrypts "Message 1".
     - Bob sends "Message 2" → Alice drains and decrypts "Message 2".
     - Alice sends "Message 3" → Bob drains and decrypts "Message 3".
     - Bob sends "Message 4" → Alice drains and decrypts "Message 4".
     - Assert: Double Ratchet DH ratchet steps advance on both sides, ephemeral keys rotate, message keys zeroized, bit-for-bit plaintext equality, zero direct socket streams.

2. **Multi-Exchange Shared Mailboxes with Option 3A Per-Exchange Channel & Ratchet Isolation (`MultiExchangeSameRelayReplyIntegrationTests`)**:
   - Start Exchange R1 (`Friends-Exchange`) and Exchange R2 (`Work-Exchange`).
   - Both Alice and Bob register mailboxes on R1 **and** R2 (publishing PreKeyBundles on each shared Exchange).
   - Alice and Bob pair and exchange messages on R1, and independently pair and exchange messages via R2.
   - Verify Option 3A isolation:
     - Distinct `ChannelId`s (`ChannelId.DeterministicForDirect(alice, bob, R1)` != `ChannelId.DeterministicForDirect(alice, bob, R2)`) and completely independent Double Ratchet sessions per Exchange.
     - Messages sent to Bob's mailbox on R1 are replied to exclusively via R1, and messages sent to Bob's mailbox on R2 are replied to exclusively via R2 (strict same-Exchange reply routing, zero cross-Exchange federation, zero direct peer connections).

3. **Offline Mailbox Buffering & Outbox Retry (`OfflineBufferingIntegrationTests`)**:
   - Bob pauses drain (`bob.StopDrainAsync()`).
   - Alice sends 3 messages ("Msg 1", "Msg 2", "Msg 3") via R1.
   - Verify R1's SQLite mailbox queue depth == 3.
   - Bob resumes drain (`bob.ResumeDrainAsync()`); drains, persists, decrypts all 3 messages in order; R1 queue depth returns to 0.
   - **Outbox Outage Retry**: Stop R1 (`r1.StopAsync()`). Alice sends "Msg 4" → `OutboxDispatchHost` fails delivery and schedules exponential backoff. Restart R1 (`r1.RestartAsync()`) → Alice's outbox retries, delivers to R1; Bob drains and decrypts.

4. **Group V2 via Relay (`RelayedGroupChatIntegrationTests`)**:
   - Alice, Bob, Carol provisioned on Relay R1.
   - Pairwise 1:1 sessions established between all pairs.
   - Alice creates Group V2 (`RelayGroupLedger`), generates Sender Key, distributes keys to Bob and Carol via pairwise `RelayedOneToOne`.
   - Bob and Carol join, generate Sender Keys, distribute via `RelayedOneToOne`.
   - **Symmetric Group Messaging**:
     - Alice sends group message → Bob and Carol both decrypt with Alice's Sender Key.
     - Bob sends group message → Alice and Carol both decrypt with Bob's Sender Key.
     - Carol sends group message → Alice and Bob both decrypt with Carol's Sender Key.
   - **Epoch Rotation Gate**:
     - Alice removes Carol; Alice rotates group epoch and distributes new Sender Key to Bob only.
     - Alice sends message → Bob decrypts successfully; Carol receives envelope but cannot decrypt (ratchet rejection).

5. **RelayHostingPolicy Enforcement & Rate Limiting (`RelayPolicyEnforcementIntegrationTests`)**:
   - Relay with `RelayHostingPolicy.HostManaged` (`RegistrationStrategy = ApprovalRequired`, `AutoApproveLocalhost = true`):
     - Pre-associated local Persona (with `LocalRelayAssociation` record written to local DB) auto-approves immediately over real localhost TLS/gRPC.
     - Non-associated caller attempting `RegisterMailbox` is staged in `PendingMailboxRegistrations` (`PENDING_APPROVAL`) until operator approves via `IExchangeRegistrationModerationService`; after approval, mailbox activates cleanly.
     - Operator-rejected or blocked applicant triggers silent connection abort (`HttpContext.Abort()` — zero gRPC error status returned) on retry.
   - Relay with `RegistrationStrategy = Closed` (`AutoApproveLocalhost = false`):
     - New registration attempts trigger silent connection abort (`HttpContext.Abort()`), while existing active mailboxes continue draining and delivering normally.
   - Authenticated `FetchPreKeyBundle` per-member sliding-window rate limiting: rapid burst beyond configured window returns `StatusCode.ResourceExhausted`.
   - Relay with `RelayFeatureFlags.PreKeys` disabled: `FetchPreKeyBundle` returns `StatusCode.PermissionDenied`.
   - Cumulative mailbox bytes exceeding `MaxMailboxBytes` rejected with `StatusCode.ResourceExhausted`, and unauthenticated `EnqueueMailbox` without valid `DeliveryToken` rejected with `StatusCode.PermissionDenied`.

6. **Multi-Peer Invitation Safety, SPK Rotation & MitM Pinning (`MultiPeerInvitationIntegrationTests`)**:
   - Bob exports a single `PeerInvitation` pointing to R1.
   - Alice and Carol concurrently connect using the exact same invitation URI.
   - Verify R1 issues **two distinct OTPKs** (atomic dequeue via `RelayOneTimePreKeys`; remaining OTPK count decrements by 2) so both Alice and Carol establish independent **4-DH** Double Ratchet sessions with Bob with zero cross-talk.
   - **SPK Rotation Resilience**: Bob rotates his Signed PreKey via `PreKeyMaintenanceService` and publishes the new SPK to R1; Dave connects using Bob's **original** invitation URI and succeeds without re-issuing the link.
   - **MitM Key-Swap Rejection**: Malicious/tampered relay returns a `PreKeyBundle` with a different `IdentityKey` (even with a self-consistent SPK signature); invitee aborts with `IDENTITY_KEY_MISMATCH` and writes zero contact/channel/routing rows to SQLite.
   - **Re-Invitation Key-Change Guard**: Re-importing an invitation for an existing contact with a different `IdentityKey` fails with `IDENTITY_KEY_CHANGED` (zero DB mutation).

### 7.4 Build Gate
- `dotnet test Percolator.ApplicationIntegrationTests` passes 100% deterministically without race flakes.

---

## Step 8: Critical Review & Anti-Shortcut Audit — `Percolator.ApplicationIntegrationTests`

### 8.1 Audit Checklist
- Zero `Task.Delay` or `Thread.Sleep` polling in tests (enforce `TaskCompletionSource` with bounded `CancellationTokenSource`).
- Zero cross-node RAM peeking: nodes communicate strictly over localhost TLS/gRPC; assertions verify public client ports and event subscriptions.
- Zero hardcoded loopback ports: all servers bind to port 0 (OS dynamic port assignment).
- Zero mock crypto engines: all nodes use real `SignalCryptoEngine` with Ed25519/X25519/AES-GCM.
- Zero legacy Direct P2P remnants in `Percolator.ApplicationIntegrationTests`.
- SQLite in-memory persistence: keeper connection pattern used throughout.
- Code deduplication: all test suites utilize `TestNetworkTopology`, `TestRelayServer`, and `TestPersonaNode` (zero copy-paste DI setups).

### 8.2 Full Solution Test Gate
- `dotnet build Percolator.Domain.Kernel`
- `dotnet test Percolator.Domain.Tests`
- `dotnet test Percolator.Domain.IntegrationTests`
- `dotnet test Percolator.Apps.FileTransfer.Domain.Tests`
- `dotnet test Percolator.Application.Tests`
- `dotnet test Percolator.Apps.Chat.Tests`
- `dotnet test Percolator.Apps.FileTransfer.Tests`
- `dotnet test Percolator.Apps.Discovery.Tests`
- `dotnet test Percolator.Infrastructure.Tests`
- `dotnet test Percolator.Infrastructure.Windows.Tests`
- `dotnet test Percolator.ApplicationIntegrationTests`
- All 11 suites build and pass 100%. (`Percolator.Desktop`, `Percolator.Simulator` expected to fail build.)

---

## Step 9: Simulator UI/UX Design (`Percolator.Simulator/ui-design.md`)

### 9.1 Deliverable
- Create `Percolator.Simulator/ui-design.md` (design specification ONLY; zero production code edits in this step).

### 9.2 Layout Architecture (Dense Multi-Node Workbench)
- **Visual Structure & Grid Hierarchy**:
  ```
  +----------------------------------------------------------------------------------------------------+
  | TOP GLOBAL ACTION BAR (Height: 44px, Dense, Monospace Accent)                                      |
  | [+ Persona]  [+ Relay]  |  [Quick Pair (1:1)]  [New Group]  |  [Presets v]  [Pause Net]  | [Gear v] |
  +--------------------------+-------------------------------------------------------------------------+
  | LEFT RAIL: TOPOLOGY      | CENTER WORKSPACE: CONTEXTUAL INSPECTOR & LIVE OPERATIONS                |
  | (Width: 300px, Splitter) | (Flex / Tabbed Splitter)                                                |
  |                          |                                                                         |
  | [ RELAYS (2)        [+] ]| When Persona Selected:                                                  |
  | > Relay-1 (Open)   [ON]  | - Header: Avatar, Name, PublicIdentityId (hex/copy), Linked Relays badge |
  |   - Port: 51234, Q: 3    | - Tab 1: Live Chat & Conversations (Contacts list + Message History +   |
  | > Relay-2 (Appr.)  [ON]  |          Cryptographic Badges + Rich Composer)                          |
  |                          | - Tab 2: Security & Ratchet State (Identity Key, SPK, OPKs left,        |
  | [ PERSONAS (3)      [+] ]|          Active Channels list, DH Ratchet step count, root keys)        |
  | > Alice (2 relays) [ON]  | - Tab 3: Relay Mailboxes (linked relays table, register new, token IDs) |
  |   - 1 pending, 0 unread  | - Tab 4: Pending Inbound Handshakes (TOFU Approve / Reject)             |
  | > Bob   (1 relay)  [ON]  |                                                                         |
  | > Carol (1 relay)  [OFF] | When Relay Selected:                                                    |
  |                          | - Tab 1: Live Policy Editor (RegistrationStrategy, AutoApproveLocalhost)|
  |                          | - Tab 2: Pending Registrations (Approve / Reject / Block applicants)    |
  |                          | - Tab 3: Mailbox Queues (Blinded tokens, queue depths, Purge button)    |
  |                          | - Tab 4: Hosted PreKeys & Group Ledgers                                 |
  +--------------------------+-------------------------------------------------------------------------+
  | BOTTOM DOCK: PROTOCOL TELEMETRY & WIRE INSPECTOR (Collapsible, Height: 200px, Splitter)            |
  | Filter: [Search...] [All Personas v] [All Relays v] [Clear] [Auto-Scroll [x]]                      |
  | Columns: [Timestamp] [Source Node] [Dest/Relay] [RPC / Event] [Envelope / Key ID] [Status]         |
  | Details Drawer: Displays serialized protobuf fields, TLS handshake metadata, ratchet step info    |
  +----------------------------------------------------------------------------------------------------+
  ```
- **Dimensions & Styling Rules**:
  - Window defaults: `1400x900` minimum, resizable.
  - Left rail: default width `300px`, resizable via splitter (`220px` min, `450px` max).
  - Bottom dock: default height `200px`, resizable via splitter (`120px` min, `500px` max), collapsible with a single chevron toggle.
  - Typography: System UI sans-serif for labels; Monospace (`Cascadia Code`, `Consolas`) for cryptographic fingerprints, blinded routing tokens, port numbers, and telemetry wire dumps.
  - Color palette: High-contrast dark theme; emerald green for active/listening nodes, amber for pending handshakes/queued envelopes, crimson for stopped/disconnected nodes or policy rejections.

### 9.3 Action Tier Categorization

#### Tier 1: High-Frequency Actions (1-Click, Top Toolbar & Left Rail)
| Action | Placement | Visual Element | Trigger & Immediate Outcome |
| :--- | :--- | :--- | :--- |
| **`+ Persona`** | Top Bar | Button (`Accent`) | Opens modal: auto-generates word-pair name (`Emerald-Otter`), default-selects first running relay. `Enter` or click `Create` boots persona node instantly. |
| **`+ Relay`** | Top Bar | Button (`Outlined`) | Spawns a new Kestrel relay on `127.0.0.1:0` (ephemeral port), defaults to `RelayRegistrationStrategy.Open`, shows green running indicator and bound port. |
| **`Quick Pair (1:1)`** | Top Bar | Button (`Outlined`) | Opens lightweight popover: `Inviter [Bob]` and `Invitee [Alice]` dropdowns (sharing an Exchange), click `Pair Now`. Executes real authenticated 1-RTT `FetchPreKeyBundle` + pinned `IdentityKey` verification + authenticated 4-DH X3DH handshake (`SendInitialHandshake`) and transitions to unauthenticated Sealed Sender after approval. |
| **`New Group`** | Top Bar | Button (`Outlined`) | Opens group wizard: title input, creator dropdown, checklist of contacts to invite via pairwise 1:1 relay channels. |
| **`Pause/Resume Traffic`** | Top Bar | Toggle Button | Pauses outbound gRPC delivery across all nodes (red indicator); lets developers inspect queues before flushing. |
| **`Toggle Persona Online`** | Left Rail | Toggle Switch (`ON/OFF`) | Pauses/resumes `RelayMailboxDrainHost` on the persona without disposing state (simulates client offline/sleep and reconnect drain). |
| **`Toggle Relay Running`** | Left Rail | Toggle Switch (`ON/OFF`) | Calls `StopAsync()` / `StartAsync()` on `KestrelPeerTransportServer` (simulates relay crash to test offline buffering and outbox retry). |
| **`Send Chat Message`** | Center Composer | Button / `Enter` | Encrypts message via active Double Ratchet / Sender Key, enqueues to outbox, dispatches via relay. |
| **`Switch Selected Node`** | Left Rail | Row Selection | Instantly swaps Center Workspace between Persona inspector and Relay inspector. |

#### Tier 2: Medium-Frequency Actions (Contextual Inspector Tabs)
| Context | Action | Controls & Visual Feedback |
| :--- | :--- | :--- |
| **Persona** | **Link Relay** | Dropdown of running relays, `Link Mailbox` button. Performs mailbox registration, publishes SPK + OTPKs via `PreKeyMaintenanceService`, and adds target to `DrainScheduler`. |
| **Persona** | **Export Invitation** | Select linked relay. Outputs compact read-only `percolator://invite/<base64url>` (4 fields: pinned `IdentityKey` + relay endpoint/token; zero `DeliveryToken`) and `Copy` button. |
| **Persona** | **Connect via Invite** | Monospace text input, `Connect` button. Fetches consumable `PreKeyBundle` via authenticated `FetchPreKeyBundle`, verifies pinned `IdentityKey` + Ed25519 SPK signature, and enqueues authenticated 4-DH PreKeyMessage (`SendInitialHandshake`) to peer's relay mailbox. |
| **Persona** | **Approve / Reject Handshake** | Pending banner with peer identity key hex; buttons: `Approve (TOFU)`, `Reject`, `Block`. |
| **Persona** | **Group Membership** | Member list table with role badges (`Owner`, `Member`). `+ Invite Member` button; 1-click `Kick` button (triggers epoch rotation and key redistribution). |
| **Persona** | **Send Attachment** | Attachment paperclip icon: opens file picker, stages file, uploads encrypted blob to relay chunk store, sends payload referencing blob locator. |
| **Relay** | **Edit Policy** | Radio group: `RegistrationStrategy` (`ApprovalRequired`, `Open`, `Closed`). Checkbox: `AutoApproveLocalhost` (default `ON`). Checkboxes: `RelayFeatureFlags` (`PreKeys`, `Profiles`, `Blobs`, `Groups`, `Discovery`). Quotas: `Max Mailbox Bytes`, `Max Mailboxes`, `Envelope TTL`. `Apply Changes` button (hot-reloads in memory). |
| **Relay** | **Moderate Registrations** | Table of pending applicants (`ApplicantRoutingToken`, `RequestedAtUtc`) with `Approve`, `Reject`, and `Block` buttons invoking `IExchangeRegistrationModerationService`. |
| **Relay** | **Inspect Queues** | Table: `BlindedRoutingToken` (hex), `Envelope Count`, `Queue Size (Bytes)`, `Oldest Age`. Button: `Purge Expired`. (Payload strictly opaque ciphertext). |
| **Relay** | **Inspect PreKeys** | Table: `Routing Token`, `Device ID`, `Available OTPKs Count` (warning badge if < 35), `SPK Expiry`. |

#### Tier 3: Infrequent Actions (Buried in Settings Menu & Presets Dropdown)
- **Settings Menu (`Gear` Icon)**:
  - `Auto-Accept Handshakes` (global toggle: on/off, default `OFF`).
  - `Auto-Approve Group Invitations` (on/off, default `OFF`).
  - `Network Chaos`:
    - Artificial latency slider: `0 ms` to `2000 ms` injected into gRPC client handlers.
    - Packet drop rate slider: `0%` to `25%` drop rate to test outbox exponential backoff.
  - `Log Verbosity`: `Wire/Trace`, `Information`, `Warning Only`.
  - `Cryptographic Material Logging`: Toggle unsafe key dumping (developer debugging only).
  - `Reset Environment`: Tears down all nodes and clears all in-memory SQLite tables.
- **Scenario Presets Dropdown**:
  - `1:1 Double Ratchet Symmetric Exchange` (boots 1 relay, 2 personas, pairs them, sends 4 messages).
  - `Multi-Exchange Shared Mailboxes` (boots 2 relays `Friends` and `Work`, Alice and Bob linked to both, verifies independent Option 3A channels/ratchets and same-Exchange replies on each).
  - `Offline Mailbox Buffering & Recovery` (pairs, turns Bob offline, Alice sends 3 messages, turns Bob online, verifies drain).
  - `Group V2 Epoch Rotation Eviction` (boots 3 personas, creates group, evicts 1 member, verifies ratchet key isolation).

### 9.4 Detailed Component Specifications

#### A. Top Global Action Bar
- Fixed height `44px`, horizontal flex layout, compact 4px padding.
- Left section: Primary actions `+ Persona`, `+ Relay` (prominent icons, distinct accent colors).
- Center section: Protocol orchestration `Quick Pair (1:1)`, `New Group`.
- Right section: `Presets` dropdown button, `Pause Traffic` toggle button, `Settings` gear dropdown button.

#### B. Left Rail — Topology & Entities Tree
- Header with entity count badges: `RELAYS (N)` and `PERSONAS (M)`.
- **Relay Row Element**:
  - Title: Name (e.g., `Relay-1`), Host and Port (`127.0.0.1:51234`).
  - Badges: Registration strategy (`Open` [green], `Approval` [amber], `Closed` [crimson]), Pending registrations badge (`! N` if > 0), Queue depth (`Q: 3`).
  - Controls: Small inline `[ON/OFF]` toggle switch that starts or stops the listening Kestrel server.
- **Persona Row Element**:
  - Avatar / Identicon (generated from Ed25519 identity key).
  - Title: Persona name (e.g., `Alice`), truncated Identity ID (`#a8f1...`).
  - Badges: Linked relays count (`2 Relays`), Pending handshakes indicator (`! 1` in amber).
  - Controls: Small inline `[ON/OFF]` toggle switch that pauses or resumes the `RelayMailboxDrainHost`.

#### C. Center Workspace — Contextual Inspector
- Switches context based on Left Rail selection:
- **When Persona Selected**:
  - **Header**: Large Identicon, Persona Name, full `PublicIdentityId` hex with 1-click copy button, linked relays list summary.
  - **Tab 1: Live Chat & Conversations**:
    - Left column (200px): Contacts and Group chats roster with unread badges (scoped per Exchange per Option 3A).
    - Right column: Message timeline.
    - Message Bubble: Sender name, timestamp, decrypted plaintext.
    - **Cryptographic badge on every message**:
      - 1:1: `Double Ratchet: Step N=4, DH: #2` (click opens flyout showing ratchet chain keys).
      - Group: `SenderKey: Epoch 1, Index 3`.
      - Route badge: `Relayed: Relay-1 [127.0.0.1:51234]`.
    - Bottom: Monospace chat input box with `Send` button (Enter to send, Shift+Enter for newline) and attachment button.
  - **Tab 2: Cryptographic & Ratchet State**:
    - Identity Key Fingerprint (`Ed25519: <hex>`).
    - Signed Pre-Key (`SPK ID`, validity period).
    - One-Time Pre-Key pool status (`OPKs remaining: 98 / 100`).
    - Active Sessions Table: Peer Identity, Exchange, Channel ID, Send Chain Key, Receive Chain Key, DH Ratchet count.
  - **Tab 3: Relay Mailboxes**:
    - Table of linked relays: Endpoint URI, Blinded Routing Token, Delivery Token, Drain Stream Status (`Connected`, `PendingApproval`, `Reconnecting`, `Stopped`).
    - Actions: `Link New Relay`, `Refresh Tokens`, `Unlink`.
  - **Tab 4: Inbound Handshakes**:
    - List of pending contact requests: Peer Identity Key hex, PreKey used, timestamp.
    - Actions: `Approve (TOFU)`, `Reject`, `Block`.
- **When Relay Selected**:
  - **Tab 1: Live Policy Editor**:
    - Radio buttons: `RegistrationStrategy` (`ApprovalRequired`, `Open`, `Closed`).
    - Checkbox: `AutoApproveLocalhost` (default `ON`).
    - Checkboxes: `PreKeys`, `Profiles`, `Blobs`, `Groups`, `Discovery`.
    - Number inputs: `Max Mailbox Bytes` (e.g. `10485760`), `Max Mailboxes` (e.g. `1024`), `Envelope TTL` (e.g. `604800s`).
    - Action: `Apply Policy Changes` button (hot-reloads without restart).
  - **Tab 2: Pending Registrations**:
    - Table: `ApplicantRoutingToken` (hex), `Status`, `RequestedAtUtc`.
    - Actions: `Approve`, `Reject`, `Block` (invokes `IExchangeRegistrationModerationService`).
  - **Tab 3: Mailbox Queues Inspector**:
    - Table: `BlindedRoutingToken` (hex), `Envelopes Count`, `Oldest TTL`, `Queue Size (KB)`.
    - Action: `Purge Expired Envelopes`.
    - **Opacity Rule**: Queue inspector displays strictly opaque ciphertext metadata; never plaintext.
  - **Tab 4: Hosted PreKeys & Group Ledgers**:
    - PreKeys table: `Owner Routing Token`, `Device ID`, `Available OTPKs Count`.
    - Group Ledgers table: `Group ID`, `Epoch`, `Commit Count`.

#### D. Bottom Dock — Protocol Telemetry & Wire Inspector
- Collapsible split pane showing live chronological stream of gRPC RPC calls and domain events:
- Toolbar: Search input, Persona filter dropdown, Relay filter dropdown, Event Kind toggles (`gRPC`, `Outbox`, `Events`, `Errors`), `Clear Log`, `Auto-Scroll [x]`.
- Telemetry Grid Columns:
  1. `Timestamp` (`HH:mm:ss.fff`)
  2. `Source` (Node name)
  3. `Destination / Relay` (Endpoint URI)
  4. `RPC / Method` (`FetchPreKeyBundle`, `SendInitialHandshake`, `EnqueueMailbox`, `ConnectMailboxStream`, `PublishPreKeyBundle`, `RegisterMailbox`, etc.)
  5. `Payload / Key ID` (Envelope routing token or group ID)
  6. `Status` (`OK`, `PendingApproval`, `PermissionDenied`, `ResourceExhausted`, `ConnectionAborted`, `InFlight`)
- Row Selection Drawer: Split pane showing the exact serialized Protobuf request/response fields and byte sizes.

### 9.5 Zero-Shortcut Rules Encoded in UX
1. **Zero RAM Peeking in Quick Pair**:
   - `Quick Pair` MUST invoke the public driving ports: Bob exports a 4-field `PeerInvitation` (pinned `IdentityKey`, zero `DeliveryToken`), Alice calls `ConnectWithInvitationAsync` (fetching a consumable `PreKeyBundle` via authenticated `FetchPreKeyBundle`, verifying pinned `IdentityKey` + SPK signature, and sending an authenticated 4-DH PreKeyMessage via `SendInitialHandshake` to their shared relay), Bob drains via `RelayMailboxDrainHost` and approves (delivering `DeliveryToken` inside his E2EE reply so subsequent messages switch to unauthenticated Sealed Sender). Zero synthetic channel creation.
2. **Relay Prerequisite Gate**:
   - A Persona with 0 linked relays cannot send chat messages or export invitations. The UI disables these actions with a warning tooltip: *"Link a Relay to enable messaging."*
3. **Zero Direct P2P Remnants**:
   - Zero direct IP/port inputs, zero port-range presets, zero direct socket status indicators. All transport is relay-mediated.
4. **Relay Opacity Enforced in Inspector**:
   - When inspecting Relay mailbox queues or wire telemetry, payload data is displayed strictly as `[EncryptedPackage: 248 bytes]`. Plaintext is only visible inside recipient Persona chat timeline after Double Ratchet decryption.

### 9.6 State Diagrams & Keyboard Shortcuts
- **State Flows in Specification**:
  - Persona state: `Unlinked` → `RelayLinked` → `Ready`.
  - 1:1 Pinned-Identity PreKey handshake: `ExportInvite` → `Auth FetchPreKeyBundle + PinVerify` → `Auth 4-DH SendInitialHandshake` → `PendingApproval` → `Active (Unauth Sealed Sender)`.
  - Group V2 flow: `CreateGroup` → `PairwiseKeyDistribution` → `EpochActive` → `EpochRotation`.
- **Global Keyboard Shortcuts**:
  - `Ctrl+Shift+P`: New Persona modal.
  - `Ctrl+Shift+R`: New Relay.
  - `Ctrl+Shift+H`: Quick Pair wizard.
  - `Ctrl+Shift+G`: New Group wizard.
  - `Ctrl+Shift+T`: Toggle bottom telemetry dock collapse/expand.
  - `Ctrl+Space`: Pause/resume global network traffic.

---

## Step 10: Critical Review & Refinement — `Percolator.Simulator/ui-design.md`

### 10.1 Review Checklist
- Verify every hybrid capability is operable from the UI:
  - Shared-Exchange & multi-Exchange topologies (Alice and Bob sharing a single Exchange, or sharing multiple Exchanges such as Friends + Work with Option 3A independent channels/ratchets and same-Exchange reply isolation).
  - Relay failure/restart simulation (offline buffering & Outbox retry visibility).
  - `RelayHostingPolicy` experimentation (`RegistrationStrategy`: `ApprovalRequired` moderation queue, `Open`, `Closed` silent hang-up; `AutoApproveLocalhost` toggle; disabling individual `RelayFeatureFlags`).
  - Multi-peer pinned-identity invitations & Relay-Hosted PreKeys (connecting 2+ personas from one invite, observing distinct OTPK dequeue on Relay Hosted PreKeys tab, and verifying SPK rotation resilience).
  - Choice A visibility in Protocol Telemetry (`FetchPreKeyBundle` + `SendInitialHandshake` on authenticated channel → `EnqueueMailbox` on unauthenticated channel after approval).
  - Group V2 creation, member invite, epoch rotation, member removal.
  - Rich chat (replies, edits, reactions, attachments via `RelayHosted` blob store, encrypted profile sync).
- Verify density vs discoverability: high-frequency actions ≤ 1 click; infrequent settings buried in menu.
- Verify zero Direct P2P terminology or workflows remain in `ui-design.md`.
- Revise `Percolator.Simulator/ui-design.md` until all gaps are closed.

---

## Step 11: Simulator Implementation & Tests

### 11.1 Deletions & Legacy Removal
- Delete `Percolator.Simulator/Features/DirectChat/` completely:
  - `DirectHandshakeOrchestrator.cs` (deleted: contained RAM-peeking direct socket shortcuts).
  - `DirectChatCoordinator.cs`.
- Delete `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs` (contained deleted `PortRangePrivacyPreset`).
- Delete obsolete direct scenarios in `Percolator.Simulator/Scenarios/`:
  - `01-direct-handshake-and-chat.json` (superseded by `01-relayed-handshake-and-chat.json`).
- In `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`:
  - Remove `KestrelPeerTransportServer` field, constructor parameter, and startup call. (Persona hosts are pure clients; they never run Kestrel).
  - Remove `GetLocalDirectEndpoint()` and direct IP/port advertisement methods.
- In `Percolator.Simulator/Features/GroupMessaging/GroupChatCoordinator.cs`, `ProfileSyncCoordinator.cs`, `RichChatActionCoordinator.cs`:
  - Remove all references to `DeliveryRoute.DirectP2P`, `ProfileSourceType.DirectP2P`, and `IStreamRegistryManagement`.
- **Note on `Percolator.Desktop.Generators`**:
  - Contains only `DialogWindowGenerator` for `Percolator.Desktop` dialog window mapping. Zero transport, network, crypto, or protocol dependencies. Requires **zero changes**.

### 11.2 Architectural Invariants & Anti-Shortcut Rules
- **`WorldState` Access is a Code Smell**:
  - `ISimulatedWorldStateService` is strictly permitted **only** for top-level shell UI topology listing (populating the Left Rail list of running personas and relays).
  - **Zero Cross-Peer RAM Peeking**: Any coordinator, service, or ViewModel accessing `_worldState.GetPersonaHost(targetId)` to peek at a peer's identity key, prekey bundle, channel state, or repository is a **strict architectural violation**.
  - All communication between peers must travel over the actual localhost TLS/gRPC network interface via `TransportDispatcher` and `RelayMailboxDrainHost`.
- **AI Implementer Escalation Rule**:
  - If an AI implementer encounters difficulty orchestrating multi-node flows through public driving ports, or is tempted to bypass network delivery via `WorldState` lookups, it **MUST STOP and ask the user for guidance** rather than inventing a synthetic shortcut.
- **Thread Marshaling & Disposables**:
  - All background gRPC/event notifications crossing into ViewModels must dispatch to the Avalonia UI thread via `Dispatcher.UIThread.Post(...)`.
  - Every `R3.Observable` or `IEventSubscriber` subscription in ViewModels and Coordinators must be registered in a `CompositeDisposable` and disposed on view teardown or node removal.

### 11.3 Greenfield Implementation per `ui-design.md`

#### A. Node Hosts (`Features/Topology/`)
- `SimulatedRelayHost`:
  - Boots `KestrelPeerTransportServer` on `127.0.0.1:0` (ephemeral port).
  - Holds `RelayHostingPolicyHolder` (`IRelayHostingPolicyProvider`) for live policy mutations.
  - Exposes `RelayEndpoint Endpoint { get; }`.
  - Exposes `Task StopAsync()` and `Task StartAsync()` for outage simulation.
- `SimulatedPersonaHost`:
  - Runs pure client stack: keeper in-memory `SqliteConnection`, `SignalCryptoEngine(new CryptographyOptions())`, `TransportDispatcher`, per-Persona `PeerGrpcChannelFactory`.
  - Hosts background workers: `RelayMailboxDrainHost` (subscribes `DrainScheduler.RunAsync`) and `OutboxDispatchHost`.
  - Exposes `Task StopDrainAsync()` and `Task ResumeDrainAsync()` for offline simulation.

#### B. Greenfield ViewModels & Views
- **Shell Layout**:
  - `Features/Shell/WorkbenchViewModel.cs` & `WorkbenchView.axaml`: main 3-pane grid (Left Rail, Center Flex, Collapsible Bottom Dock).
  - `Features/Shell/TopGlobalToolbarViewModel.cs` & `TopGlobalToolbarView.axaml`: Tier 1 actions (`+ Persona`, `+ Relay`, `Quick Pair`, `New Group`, `Pause Traffic`, `Presets`, `Settings`).
  - `Features/Shell/TopologyTreeViewModel.cs` & `TopologyTreeView.axaml`: Left rail lists for Relays (`RelayCardViewModel` with Kestrel `[ON/OFF]`) and Personas (`PersonaCardViewModel` with Drain `[ON/OFF]`).
- **Contextual Inspectors**:
  - `Features/Inspectors/PersonaInspectorViewModel.cs` & `PersonaInspectorView.axaml`:
    - Tab 1: `ChatTimelineViewModel` (message bubbles with `Double Ratchet: Step N` or `SenderKey: Epoch E` cryptographic badges, monospace composer).
    - Tab 2: `RatchetStateViewModel` (visualizes `SignalCryptoEngine` identity key fingerprint, SPK expiry, OPK pool count, active Exchange-scoped DH chains).
    - Tab 3: `RelayMailboxesViewModel` (table of linked relays, registration tokens, drain statuses; `Link Relay` action).
    - Tab 4: `PendingHandshakesViewModel` (inbound contact requests; `Approve (TOFU)` / `Reject` / `Block`).
  - `Features/Inspectors/RelayInspectorViewModel.cs` & `RelayInspectorView.axaml`:
    - Tab 1: `PolicyEditorViewModel` (radio `RegistrationStrategy`: `ApprovalRequired` / `Open` / `Closed`, `AutoApproveLocalhost` toggle, 5 `RelayFeatureFlags` checkboxes [`PreKeys`, `Profiles`, `Blobs`, `Groups`, `Discovery`], `MaxMailboxBytes`, `MaxMailboxes`, `EnvelopeTtl`; hot-reloads `RelayHostingPolicyHolder` in memory).
    - Tab 2: `PendingRegistrationsViewModel` (lists staged `PendingMailboxRegistrations`; `Approve` / `Reject` / `Block` actions via `IExchangeRegistrationModerationService`).
    - Tab 3: `MailboxQueuesViewModel` (table of hosted mailboxes: `BlindedRoutingToken`, envelope count, queue size; strictly opaque ciphertext metadata; `Purge Expired` button).
    - Tab 4: `HostedPreKeysViewModel` (registered prekey bundles and remaining OPK gauges).
- **Protocol Telemetry**:
  - `Features/Telemetry/ProtocolTelemetryViewModel.cs` & `ProtocolTelemetryView.axaml`: filterable live feed of gRPC RPC calls and outbox events (`Timestamp`, `Source`, `Dest/Relay`, `RPC/Method`, `Payload/Key ID`, `Status`) with split-pane protobuf inspector drawer.

#### C. `QuickPairCoordinator` Code Contract (`Features/Pairing/QuickPairCoordinator.cs`)
```csharp
// Percolator.Simulator/Features/Pairing/QuickPairCoordinator.cs
public sealed class QuickPairCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorSettingsService _settings;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public QuickPairCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorSettingsService settings,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public async Task<bool> ExecutePairAsync(
        SimulatedNodeId inviterId,
        SimulatedNodeId inviteeId,
        CancellationToken ct = default)
    {
        var inviter = _worldState.GetPersonaHost(inviterId);
        var invitee = _worldState.GetPersonaHost(inviteeId);
        if (inviter == null || invitee == null || inviter.Id == invitee.Id)
        {
            _toastService.ShowToast("Pairing Failed", "Must select two distinct active personas.", ToastSeverity.Warning);
            return false;
        }

        var inviterRelay = inviter.GetPrimaryLinkedRelay();
        if (inviterRelay == null)
        {
            _toastService.ShowToast("Pairing Failed", $"{inviter.DisplayName} has no linked relay.", ToastSeverity.Error);
            return false;
        }

        // 1. Inviter exports 4-field PeerInvitation (pinned IdentityKey + Exchange routing coordinates, zero DeliveryToken)
        using var inviterScope = inviter.Services.CreateScope();
        var invService = inviterScope.ServiceProvider.GetRequiredService<IPeerInvitationService>();
        var inviteResult = await invService.CreatePeerInvitationAsync(
            inviter.PublicIdentityId, inviterRelay.Endpoint, ct);
        if (inviteResult.IsFailure)
        {
            _toastService.ShowToast("Pairing Failed", inviteResult.Error.Description, ToastSeverity.Error);
            return false;
        }
        var inviteUri = PeerInvitationCodec.Encode(PeerInvitationPayload.FromDomain(inviteResult.Value));

        // 2. Invitee connects via public driving port (authenticated 1-RTT FetchPreKeyBundle + pinned IdentityKey verify + authenticated 4-DH SendInitialHandshake)
        using var inviteeScope = invitee.Services.CreateScope();
        var workflow = inviteeScope.ServiceProvider.GetRequiredService<IOutboundConnectionWorkflow>();
        var decoded = PeerInvitationCodec.Decode(inviteUri);
        var connectResult = await workflow.ConnectAsync(invitee.PublicIdentityId, decoded, TimeSpan.FromSeconds(10), ct);
        if (!connectResult.IsSuccess)
        {
            _toastService.ShowToast("Pairing Failed", connectResult.Error.Description, ToastSeverity.Error);
            return false;
        }

        _logSink.Log(new SimulatorLogEntry(DateTimeOffset.UtcNow, LogLevel.Information, invitee.DisplayName, invitee.Id,
            "Handshake", $"Fetched PreKeyBundle, verified pinned IdentityKey, and enqueued 4-DH PreKeyMessage to {inviterRelay.Endpoint.Uri} for {inviter.DisplayName}."));

        // 3. Inviter's RelayMailboxDrainHost receives envelope from relay; if auto-accept enabled, approve:
        if (_settings.AutoAcceptHandshakes)
        {
            var contactRepo = inviterScope.ServiceProvider.GetRequiredService<IContactRepository>();
            var contact = await contactRepo.GetByIdAsync(inviter.PublicIdentityId, invitee.PublicIdentityId, ct);
            if (contact != null)
            {
                contact.Approve(inviterScope.ServiceProvider.GetRequiredService<IDateTimeProvider>());
                await contactRepo.SaveAsync(contact, ct);
            }
        }

        _toastService.ShowToast("Pairing Complete", $"⚡ Handshake initiated between {inviter.DisplayName} and {invitee.DisplayName}.", ToastSeverity.Success);
        return true;
    }
}
```

#### D. Relay Policy Hot-Reload Code Contract (`RelayInspectorViewModel.cs`)
```csharp
[RelayCommand]
public void ApplyPolicy()
{
    var newPolicy = new RelayHostingPolicy(
        registrationStrategy: SelectedRegistrationStrategy,
        autoApproveLocalhost: AutoApproveLocalhost,
        enabledFeatures: SelectedFeatureFlags,
        maxMailboxes: MaxMailboxes,
        maxMailboxBytes: MaxMailboxBytes,
        envelopeTtl: TimeSpan.FromSeconds(EnvelopeTtlSeconds));

    _relayHost.Policy = newPolicy;
    _toastService.ShowToast("Policy Updated", $"Relay {_relayHost.DisplayName} policy hot-reloaded in memory.", ToastSeverity.Success);
}
```

### 11.4 Unit Tests — `Percolator.Simulator.Tests`
- Delete obsolete direct handshake/chat tests (`DirectChatCoordinatorTests`, `DirectHandshakeOrchestratorTests`).
- Add high-value ViewModel & Coordinator tests:
  - `QuickPairCoordinatorTests`: executes authenticated pinned-identity `FetchPreKeyBundle` + 4-DH X3DH flow across real simulated relay without cross-node RAM peeking.
  - `PersonaInspectorViewModelTests`: verifies invitation export requires $\ge 1$ linked relay; validates `PeerInvitationCodec` URI encoding (`percolator://invite/<base64url>`).
  - `RelayInspectorViewModelTests`: verifies `RegistrationStrategy`, `AutoApproveLocalhost`, and `RelayFeatureFlags` mutations update `IRelayHostingPolicyProvider` live in memory, and verifies pending registration approve/reject/block actions.
  - `TopologyTreeViewModelTests`: verifies Persona toggle pauses/resumes `RelayMailboxDrainHost`; verifies Relay toggle starts/stops Kestrel server.

### 11.5 Build Gate
- `dotnet build Percolator.Simulator` succeeds.
- `dotnet test Percolator.Simulator.Tests` passes 100%.

---

## Step 12: Critical Review & Remediation — Simulator

### 12.1 Audit Checklist & Anti-Shortcut Gate
- **Comprehensive `WorldState` Code Smell Audit**:
  - Run scan: `git grep "WorldState\|_worldState" -- Percolator.Simulator`
  - Verify every single occurrence:
    - PASS: Allowed solely in shell UI collections (e.g. `TopologyTreeViewModel` reading the list of nodes).
    - FAIL: Prohibited in any chat, messaging, grouping, profile sync, or handshake coordinator/service. Any instance where a coordinator looks up a target persona to bypass network serialization or read in-memory state must be refactored to go over the network.
- **Escalation Verification**:
  - Verify that the AI implementer did not invent any synthetic bypasses or cross-node RAM lookups.
- **Direct P2P Remnants Scan**:
  - `git grep "DirectP2P\|PortRangePrivacyPreset\|GetLocalDirectEndpoint" -- Percolator.Simulator Percolator.Simulator.Tests` → 0 matches.
- **Resource Management & Thread Safety**:
  - Verify zero `async void` methods (except top-level UI event handlers).
  - Verify all `R3.Observable` / `IEventSubscriber` subscriptions are tracked in `CompositeDisposable` and disposed on close.
  - Verify all UI updates from background workers dispatch via `Dispatcher.UIThread.Post(...)`.
- **UI Match Gate**:
  - Verify layout, panels, action tiers, and telemetry inspector match `Percolator.Simulator/ui-design.md`.
- **Test Gate**:
  - Re-run `dotnet test Percolator.Simulator.Tests` → 100% green before Step 13.

---

## Step 13: Desktop App & Onboarding UX Design (`Percolator.Desktop/shell-flow.md`)

### 13.1 UI/UX Terminology Standards & Constraints
- **Strict Terminology Governance in UI**:
  - Cryptographic Identity = **Persona** (never "User", "Account", or "Node").
  - Routing Service / Mailbox Host = **Exchange** (never "Relay" in UI labels, dialog tabs, buttons, or tooltips).
  - Under the hood, domain/proto types remain `RelayEndpoint` / `DeliveryRoute.RelayedOneToOne`; the UI layer strictly renders **Exchange**.
- **No Mandatory Local Exchange**:
  - A Persona can connect to **zero to many Exchanges** (external or self-hosted).
  - Personas are pure clients: they never run Kestrel transport servers, never bind listening ports, and never acquire port leases.
  - Port leases (`IPortLease`) are reserved strictly for **Self-Hosted Exchanges**.
- **Self-Hosted Exchange Stealth & Lock Teardown Invariants**:
  - Self-Hosted Exchanges strictly obey `SYS-PRE-ACTIVATION-STEALTH` (never auto-bind listening sockets prior to explicit user vault unlock and activation) and `SYS-LOCK-TEARDOWN-AND-ZEROIZATION` (locking the session immediately stops `KestrelPeerTransportServer`, closes all open sockets, releases port leases, and zeroizes memory).
- **Elimination of QR Codes**:
  - Percolator is a desktop application with no optical camera scanning support.
  - Drop QR code generation, QR image byte properties, and QR visual controls completely. All invitations are encoded links: `percolator://invite/<base64url>` with a 1-click `[Copy Invitation Link]` action.

### 13.2 Required Deltas to `Percolator.Desktop/shell-flow.md`

#### 1. Screen 2A — Identity Gate (`IdentityGateView.axaml` & `IdentityGateViewModel`)
- **Persona Cards**:
  - Remove port-status pills (`PortStatus.Ready` / `AutoSelected` / `Unavailable`) and inline port editors completely.
  - Add linked Exchanges badge: displays connectivity status (e.g. `Linked: 2 Exchanges`, `Linked: Home Exchange`, or `No Exchange Linked` [subtle grey badge]).
  - Update `PersonaLaunchDescriptor`: remove `IPortLease Lease` and `int RequestedPort` (Personas launch as pure clients).
  - Launching a Persona with 0 linked Exchanges is fully permitted (allows opening the app to connect to an external Exchange).
- **Exchange Tiles (`ExchangeTileViewModel`)**:
  - Retain port status pills (`5100 Ready`) and listener toggles exclusively on Self-Hosted Exchange tiles (listener starts only after explicit activation — zero pre-unlock binding per `SYS-PRE-ACTIVATION-STEALTH`).

#### 2. Graceful Zero-Exchange State Guidance
- When an active Persona has 0 linked Exchanges:
  - **Sidebar & Content Area Empty State**:
    - Displays an inviting call-to-action card: *"No Exchange Linked. To send or receive messages, connect to an Exchange or start your own."*
    - Action buttons: `[+ Connect to an Exchange]` (opens Add Connection dialog) and `[+ Start Local Exchange]` (opens Exchange Creation wizard).
  - **Share Invitation Dialog**:
    - Generation is disabled; displays an informative banner: *"Link an Exchange to export an invitation so peers know where to reach you."* with a button: `[+ Connect to an Exchange]`.
  - **Add Connection Dialog (`Connect with Peer` Tab)**:
    - Connection submission is disabled unless the active Persona is linked to the Exchange specified in the invitation (or offers a 1-click prompt to connect to that Exchange first).

#### 3. Share Invitation Dialog (`ShareInvitationDialog`)
- **Deletions**:
  - Delete `PortRangePrivacyPreset` (`DefaultRange` / `CustomRange` / `SinglePort`), `CustomPortMin`, `CustomPortMax`, `EndpointHosts`, `BoundPort`, and `IQrCodeGenerator` (`_qrCodePngBytes`).
- **Clean Layout & Controls**:
  - **Linked Exchange Selector**:
    - Dropdown populated with the Persona's active linked Exchanges.
    - Defaults to primary linked Exchange.
    - If only 1 Exchange is linked, rendered as a clean static label (e.g., `Via: Community Exchange (exchange.example.com)`).
  - **Output Panel**:
    - Monospace text box displaying compact `percolator://invite/<base64url>` (carries pinned `IdentityKey` + Exchange endpoint & inviter routing identifier; valid across PreKey rotations and safe to share with multiple contacts because each peer fetches a fresh OTPK from the Exchange).
    - Primary button: **`[Copy Invitation Link]`** with transient "Copied to clipboard!" confirmation toast.

#### 4. Add Connection Dialog (`AddConnectionDialog`)
- **Tabs**: **`[Connect with Peer]`** and **`[Connect to Exchange]`** (zero occurrence of the word "Relay").
- **`Connect with Peer` Tab**:
  - Input: Monospace text area with auto-paste affordance for `percolator://invite/<base64url>`.
  - **Reactive Preview Card** (renders immediately on valid URI input):
    - Remote peer Identicon + truncated pinned `IdentityKey` fingerprint (`#a8f1...`).
    - Exchange host badge: `Shared Exchange: exchange.example.com`.
    - Format validation badge: `✔ Valid Invitation Format` (or `✖ Invalid Invitation Link`).
    - Shared-Exchange status indicator: confirms active Persona has a mailbox on `exchange.example.com` (replies stay on the same Exchange); if not yet linked to that Exchange, displays inline call-to-action to join that Exchange first.
  - **Submit Action (`[Connect]`)**:
    - Executes 1-RTT connection via `IOutboundConnectionWorkflow.ConnectAsync`: checks re-invitation safety (`IDENTITY_KEY_CHANGED` / `CONTACT_BLOCKED`), fetches consumable `PreKeyBundle` from the shared Exchange, verifies the fetched bundle against the invitation's pinned `IdentityKey` + Ed25519 SPK signature, and atomically persists session + enqueues a 4-DH PreKeyMessage.
    - On verification failure (`IDENTITY_KEY_MISMATCH` / `INVALID_PREKEY_SIGNATURE` / `IDENTITY_KEY_CHANGED`), keeps dialog open and displays a prominent crimson security error banner (`DEP-DESKTOP-KEY-CHANGE-WARNING`, zero local DB writes).
    - On unreachable Exchange (`OutboundConnectionStatus.PendingOffline`), displays an inline warning so the user can retry once the Exchange is reachable.
    - On success: stages contact in `PendingOutbound` and channel in `PendingAcceptance`, closes dialog immediately, and selects the new chat in the sidebar.
    - Conversation timeline displays informational pending banner: *"Waiting for contact to approve. Messages you send will be queued securely."*
- **`Connect to Exchange` Tab**:
  - Input for Exchange host address (with optional port or port range), or link to launch the Self-Hosted Exchange wizard.

#### 5. Conversations Sidebar & Profile Inspector Drawer (Option 3A Aligned)
- **Exchange-Scoped Conversations (`shell-flow.md` Section 2 & Option 3A)**:
  - Selecting an Exchange in the Left Sidebar populates the Right Sidebar exclusively with 1:1 and Group conversations hosted on that specific Exchange (`ChannelId.DeterministicForDirect(ownerId, remotePeerId, relayEndpoint)`).
  - If two Personas share mailboxes on multiple Exchanges (e.g., `Friends-Exchange` and `Work-Exchange`), each Exchange maintains its own independent conversation thread and Double Ratchet session — replies stay strictly on the same Exchange.
- **Sidebar Tabs**:
  - Remove obsolete `DirectP2P` vs `Relayed` filter split; replace with `All` / `Unread` / `Groups`.
- **Conversation Item Badges**:
  - Status badges: `Active` (normal), `Waiting for Contact` (outbound handshake pending), `Group (N members)`.
- **Profile Inspector Drawer**:
  - Route Telemetry displays: `Exchange: exchange.example.com (Latency: 35ms)`. Zero references to LAN, STUN, or Direct IP.
- **Privacy & Security Invariant Enforcement**:
  - `SYS-CONTACT-CONSENT-GATE`: Inbound handshakes never trigger modal popups or OS toasts; strictly isolated to the bottom `[Pending Requests]` badge.
  - `SYS-BLOCK-WIRE-INDISTINGUISHABILITY`: Blocking a contact immediately zeroizes session keys and silently drops future ingress.

#### 6. Active Context Switcher Flyout & Host-Only Exchange Control Surface (`shell-flow.md` Section 5 & `ExchangeManageViewModel`)
- **Active Context Switcher Flyout (Extension of `shell-flow.md` Section 5)**:
  - Anchored to the bottom-left identity area (preserving strict No-Quick-Jump context switching).
  - Displays two clearly separated sections:
    1. **Personas**: All currently activated Personas (selecting switches the workspace to Persona messaging mode).
    2. **Self-Hosted Exchanges**: Locally hosted Self-Hosted Exchanges owned on this machine (visible **only** to the Exchange host — never exposed to regular/remote Exchange clients). Displays a subtle pending-registrations badge when applicants are awaiting approval.
  - Selecting a Self-Hosted Exchange switches the workspace into the **Exchange Control Surface** (`ExchangeControlView` / `ExchangeManageViewModel`).
- **Host-Only Exchange Control Surface (`ExchangeControlView` / `ExchangeManageViewModel`)**:
  - **Pending Mailbox Registrations** (silent zero-popup queue mirroring `SYS-CONTACT-CONSENT-GATE`):
    - Lists pending registration applicants when `RegistrationStrategy == ApprovalRequired` (`ApplicantRoutingToken` hex fingerprint + `RequestedAtUtc`).
    - Actions: **`[Approve]`**, **`[Reject]`**, and **`[Block]`** via `IExchangeRegistrationModerationService` (rejected/blocked callers receive a silent connection hang-up `HttpContext.Abort()` on retry).
  - **Registration Strategy & Localhost Fast Path**:
    - `RegistrationStrategy` radio group:
      - `Approval Required` *(Default for `RelayHostingPolicy.HostManaged` — remote callers are staged for host approval)*
      - `Open` *(Auto-approves registrations up to `MaxMailboxes`)*
      - `Closed` *(Silently hangs up on new registrations; existing mailboxes remain active)*
    - Toggle checkbox: **`Auto-Approve Local Personas (Localhost)`** *(Default `ON` — automatically approves loopback registrations backed by a `LocalRelayAssociations` DB record written by a local Persona prior to dialing localhost TLS/gRPC; zero network bypasses)*.
  - **Feature Flags, Quotas & Binding**:
    - Feature flag checkboxes: `PreKeys`, `Profiles`, `Blobs`, `Groups`, `Discovery`.
    - Quotas: `Max Mailboxes`, `Max Mailbox Bytes`, `Envelope TTL`.
    - Canonical port configuration (binds within `5100-5199` range per `DEC-CANONICAL-PORT-RANGE`).
  - **Active Mailboxes & PreKey Health**:
    - Displays strictly opaque metadata (`BlindedRoutingToken`, queued envelope count, remaining OTPKs; zero plaintext visibility).

---

## Step 14: Desktop Implementation & Remaining Compiler Fixes

### 14.1 Deletions
- `Percolator.Desktop/Common/Services/DesktopNetworkEndpointDiscoveryPort.cs`
- `Percolator.Desktop/Features/Transports/Services/OutboundPendingRetryWorker.cs`
- `Percolator.Desktop/Features/Transports/Services/IOutboundPendingRetryWorker.cs`

### 14.2 Modifications — `Percolator.Desktop`
- `Percolator.Desktop/Features/Startup/ViewModels/IdentityGateViewModel.cs` & `SelectablePersonaModel.cs`:
  - Update `PersonaLaunchDescriptor`: remove `IPortLease Lease` and `int RequestedPort` (Personas launch as pure clients).
  - Update `SelectablePersonaModel`: remove `PreferredPort`, `PortStatus`, and inline port editor properties; add `LinkedExchangeCount` / `ExchangeStatusText`.
  - In `IdentityGateViewModel`: remove port lease acquisition loop for Personas; retain port lease acquisition strictly for `_exchangeList` (`ExchangeTileViewModel`).
- `Percolator.Desktop/Features/Transports/ViewModels/ShareInvitationDialogViewModel.cs` & AXAML view:
  - Delete `PortRangePrivacyPreset`, `CustomPortMin`, `CustomPortMax`, `_boundPort`, `EndpointHosts`.
  - Delete `IQrCodeGenerator` dependency and `_qrCodePngBytes` property completely.
  - Inject linked Exchanges for active Persona; add `SelectedExchange`, `HasLinkedExchange`; generate compact 4-field `PeerInvitationCodec` payload (`percolator://invite/<base64url>`) from Persona's `PublicIdentityId` + pinned `IdentityKey` + selected Exchange's `RelayMailboxBinding` (zero `DeliveryToken` and zero ephemeral PreKey material embedded).
  - If `HasLinkedExchange == false`, disable generation and render call-to-action button to connect an Exchange.
- `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs` & `AddConnectionDialogModels.cs`:
  - Rename tab and dialog labels from "Relay" to "Exchange".
  - Decode `PeerInvitationPayload` (`percolator://invite/<base64url>`); invoke `IPeerConnectionService.ConnectWithInvitationAsync`; handle `IDENTITY_KEY_MISMATCH` / `INVALID_PREKEY_SIGNATURE` / `IDENTITY_KEY_CHANGED` / `CONTACT_BLOCKED` security errors and `PendingOffline` cleanly in the UI.
  - Validate active Persona is linked to the target Exchange (or prompt to join it) before submitting.
- `Percolator.Desktop/Features/Transports/Services/DesktopPeerConnectionService.cs`:
  - Delete `IStreamRegistryManagement` dependency and `ConnectToDirectPeerAsync`.
  - Implement `ConnectWithInvitationAsync` delegating to Persona scope's `IPeerConnectionService`.
- `Percolator.Desktop/Features/Chats/Models/ConversationSummaryModel.cs`:
  - Remove `ConversationRouteType.DirectP2P` (retain `Relayed` / `Group` or replace enum with `ConversationKind { OneToOne, Group }`).
- `Percolator.Desktop/Features/Chats/Services/ConversationStateService.cs`, `Features/Chats/ViewModels/ChatViewModel.cs`, `Features/Chats/ViewModels/ChatsSidebarViewModel.cs`:
  - Remove all `ConversationRouteType.DirectP2P` and `DeliveryRoute.DirectP2P` references; route 1:1 sends exclusively via `DeliveryRoute.RelayedOneToOne`.
  - Scope conversation threads per `(Owner, Peer, Exchange)` via Exchange-scoped `ChannelId` (Option 3A, matching `shell-flow.md` Left Sidebar Exchange selection).
  - Subscribe to `ContactOutboundAcceptedEvent` to clear `IsPendingOutbound` badge reactively.
- **Active Context Switcher Flyout & Host-Only Exchange Control Surface** (`Features/Shell/` & `Features/Startup/ViewModels/ExchangeCreationViewModel.cs`, `ExchangeManageViewModel.cs`):
  - Implement the bottom-left **Active Context Switcher Flyout** (`shell-flow.md` Section 5) listing both activated Personas and local Self-Hosted Exchanges; selecting a Self-Hosted Exchange transitions the workspace to `ExchangeControlView` / `ExchangeManageViewModel` (strictly host-only).
  - Wire `RelayHostingPolicy.HostManaged` as the default for new Self-Hosted Exchanges (`RegistrationStrategy = ApprovalRequired`, `AutoApproveLocalhost = true`).
  - Expose `RegistrationStrategy` (`ApprovalRequired` / `Open` / `Closed`), `AutoApproveLocalhost`, `RelayFeatureFlags`, quotas, and Pending Mailbox Registrations moderation (`Approve` / `Reject` / `Block` via `IExchangeRegistrationModerationService`) in `ExchangeManageViewModel`.
  - When linking a local Persona to a local Self-Hosted Exchange, write the `LocalRelayAssociation` DB pre-association record prior to connecting over localhost TLS/gRPC so `AutoApproveLocalhost` approves seamlessly with zero network bypasses.
- Desktop DI composition root (`App.axaml.cs` / `ServiceCollectionExtensions`) & Session Lock coordinator:
  - Remove `DesktopNetworkEndpointDiscoveryPort`, `OutboundPendingRetryWorker`, `IStreamRegistryManagement`, and `KestrelPeerTransportServer` from pure Persona hosts (run `KestrelPeerTransportServer` strictly inside self-hosted Exchange hosts).
  - Register `PeerGrpcChannelFactory` strictly per Persona scope (never shared across Personas).
  - Enforce `SYS-PRE-ACTIVATION-STEALTH` (never start Self-Hosted Exchange listeners before explicit unlock/activation) and `SYS-LOCK-TEARDOWN-AND-ZEROIZATION` (immediately stop all Self-Hosted Exchange Kestrel listeners, dispose channel pools, release port leases, and zeroize memory on lock).
- Fix all remaining compiler errors across `Percolator.Desktop` and `Percolator.Desktop.Tests`.

### 14.3 Unit Tests — `Percolator.Desktop.Tests`
- Delete tests for `OutboundPendingRetryWorker`, `DesktopNetworkEndpointDiscoveryPort`, `PortRangePrivacyPreset`, and `DirectP2P` sidebar filters.
- Add/update tests:
  - `ShareInvitationDialogViewModelTests`: disables generation when Persona has 0 linked Exchanges; generates valid 4-field pinned-identity `PeerInvitationCodec` URI (`percolator://invite/<base64url>`) for selected Exchange with zero `DeliveryToken` and zero ephemeral PreKey material.
  - `AddConnectionDialogViewModelTests`: rejects invalid invitation URI; validates active Persona Exchange membership; invokes `ConnectWithInvitationAsync`; surfaces security error banner on `IDENTITY_KEY_MISMATCH` and `IDENTITY_KEY_CHANGED` without closing dialog; handles `PendingOffline` cleanly.
  - `ConversationStateServiceTests`: handles `ContactOutboundAcceptedEvent` to transition `PendingOutbound` conversation to `Active`; scopes conversations strictly per `(Owner, Peer, Exchange)` (Option 3A).
  - `ExchangeCreationViewModelTests`: defaults self-hosted Exchange to `RelayHostingPolicy.HostManaged` (`ApprovalRequired`, `AutoApproveLocalhost = true`); external Exchange registration works with zero local Exchanges; session lock stops Exchange Kestrel listener immediately.
  - `ExchangeManageViewModelTests` & `ActiveContextSwitcherViewModelTests`: verifies host-only Self-Hosted Exchange context switching, live `RegistrationStrategy` / `AutoApproveLocalhost` policy updates, and pending mailbox registration Approve/Reject/Block commands via `IExchangeRegistrationModerationService`.

### 14.4 Build Gate
- `dotnet build Percolator.sln` succeeds with 0 errors.
- `dotnet test Percolator.sln` passes 100% across all test assemblies.

---

## Step 15: Critical Review & Remediation — Desktop & Full Solution Gate

### 15.1 Audit Checklist
- Solution-wide hygiene scan:
  - `git grep "DirectP2P\|DirectPeer\|DeliverDirect\|IStreamRegistry\|IDirectEndpointRegistry\|PortRangePrivacyPreset\|OutboundPendingRetryWorker\|DeliveryRouteJson\|OneTimePreKeysJson\|DefaultKeyIkm\|invite/v2\|ContactGated\|LocalOnly\|RelayAdmissionMode\|PrivatePersonal"` → 0 production/test matches (excluding historical docs/tickets).
  - `git grep "System.Text.Json\|Newtonsoft.Json\|JsonSerializer"` → 0 new occurrences; verify outbox, relay PreKeys, & invitations are 100% binary/relational/protobuf.
  - `git grep "Path.GetTempPath"` in `Percolator.Infrastructure` → 0 matches (`SYS-LOCAL-STORAGE-CONTAINMENT`).
  - `git grep "127.0.0.1\|5100"` in `Percolator.Application`, `Percolator.Apps.*`, `Percolator.Desktop` → 0 hardcoded transport fallbacks.
- Desktop UX & architectural verification:
  - Persona-only + external Exchange path works with 0 self-hosted Exchanges.
  - Self-hosted Exchange path starts `KestrelPeerTransportServer` strictly after unlock/activation (`SYS-PRE-ACTIVATION-STEALTH`) with `RelayHostingPolicy` enforced (`ApprovalRequired` moderation queue, `Open`, `Closed` silent hang-up, `AutoApproveLocalhost`), host-only Exchange Control Surface accessible via the Active Context Switcher Flyout, and immediate teardown on lock (`SYS-LOCK-TEARDOWN-AND-ZEROIZATION`).
  - Zero `async void`, zero undisposed subscriptions, zero UI-thread violations, zero direct Infrastructure repository calls from ViewModels (CQRS read/write ports only).
- Test suite value check: confirm every remaining test in `Percolator.Desktop.Tests` is high-value and low-brittle.
- Final gate: `dotnet test Percolator.sln` → 100% pass, 0 skipped regressions.

---

### Step 16: Inner-Layer Protocol Remediation & Architecture Plan — Discovery Removal, Chat Introductions & Large File Transfer

> **Execution Ordering Rule (Inner Layers Before Outer Layers):**
> Downstream Simulator and Desktop UX work for Chat Introductions and Large File Transfer **MUST NOT** be built until the inner-layer deletions, bug fixes, and data-plane protocols documented below are completed in `Percolator.Domain`, `Percolator.Application`, `Percolator.Apps.Chat`, `Percolator.Apps.FileTransfer`, and `Percolator.Infrastructure`.
>
> **QMS Lifecycle Gate (`qms-implementation-review-cycle`):** Design Inputs (`RiskRegister.md`, `SystemRequirements.md`, `Requirements.md`, `<Project>/Design.md`) MUST be updated and human-approved *before* any production code in `DISC-*`, `INTRO-*`, or `FT-*` is written.
> - **Phase 1 (`DISC-001`..`DISC-003`):** Delete `Percolator.Apps.Discovery` & Exchange discovery/rendezvous server code + fix [`PreKeyBundleProviderAdapter`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs#L132-L137).
> - **Phase 2 (`FT-001`..`FT-005`):** Build Option 2 Direct P2P File Transfer (`DataChannelDotnet` ICE + STUN + DTLS + SCTP) + Exchange blinded `SwarmTrackerService` & embedded RFC 5389 UDP STUN + optional operator-opt-in zero-disk `TransferPipe` + Group V2 swarming & offline-author seeding while unlocked (zero custom crypto).
> - **Phase 3 (`INTRO-001`..`INTRO-003` + `FT-006`):** Build E2E Friend-to-Friend Peer Introductions in `Percolator.Apps.Chat` (Ask-Before-Introducing Option A) and downstream Simulator/Desktop UI enablement.

### 16.1 QMS Impact Analysis (4-Question Rubric)

1. **Discovery Removal & Obsolete Hazards/Requirements (`RiskRegister.md`, `SystemRequirements.md`, `Percolator.Infrastructure/Requirements.md`):**
   - Because stranger discovery on an untrusted Exchange inherently exposes `RoutingToken ↔ IdentityKey` and creates a scraping/harassment oracle (`HAZ-PREKEY-POOL-STARVATION`, `HAZ-EXCHANGE-IDENTITY-LINKABILITY`), **Path 1** deletes Exchange-hosted stranger discovery completely.
   - Update `SystemRequirements.md` (`SYS-DISCOVERY-PREKEY-ISOLATION`) and `Percolator.Infrastructure/Requirements.md` (`DEP-INFRA-DISCOVERY-RENDEZVOUS-STORE`) to retire Exchange-hosted discovery/rendezvous tables & RPCs, while preserving `DEP-INFRA-PREKEY-AVAILABILITY` (fixing [`PreKeyBundleProviderAdapter`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs#L132-L137) for existing-contact handshakes).
2. **Chat Peer Introductions (`Percolator.Apps.Chat/Design.md`, `RiskRegister.md`, `SystemRequirements.md`):**
   - **No new global/exchange hazards:** Introductions travel exclusively inside existing, already-verified Double Ratchet 1:1 channels.
   - **`HAZ-INTRO-UNSOLICITED-FORWARD` (Contact Card Forwarding Without Consent):** Mitigated by **Ask-Before-Introducing (Option A)**: the introducer sends an E2E consent prompt to the target contact first, only forwarding a freshly returned `PeerInvitation` upon explicit per-introduction approval (`Allow` vs. `Decline`), and keeping recipient `PendingApproval` TOFU staging (`SYS-CONTACT-CONSENT-GATE`) intact on the introduced peer. Zero new fields on [`ProfileMetadata`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.PluginSdk/Profiles/IProfilePorts.cs#L90-L95).
3. **New File Transfer Hazards & SOUP Evaluation (`RiskRegister.md`, `soup/datachannel-dotnet.md`):**
   - **`HAZ-DIRECT-TRANSFER-IP-DISCLOSURE` (Peer-to-Peer IP Address & Network Topology Disclosure During Direct Bulk Transfer):**
     - *Cause & Harm:* Direct P2P bulk transfer requires participating peers to exchange ICE network candidates (LAN IP and STUN-discovered public WAN `IP:Port`) over their E2E channel, disclosing approximate geolocation, ISP/organization, and—if multiple personas share a host—potentially correlating personas (`UN-ADDRESS-PRIVACY` / `SYS-RELAY-ONLY-CLOAKING` trade-off). In multi-peer Group V2 swarms, ICE signaling and direct DataChannel connections expose direct endpoints among participating group members.
     - *Controls:* Direct bulk transfers are restricted strictly to **approved contacts / authorized Group V2 swarm peers** holding E2E-negotiated session credentials (never unknown/pending peers), ICE signaling travels strictly over Double Ratchet / Group V2 (never plaintext on the Exchange), chat/group/profile traffic remains strictly Exchange-relayed (`SYS-RELAY-ONLY-CLOAKING`), and the UI displays an explicit, unavoidable **IP-disclosure notice** before initiating or accepting any file transfer (*"Direct P2P transfer shares your IP address with `<Contact>` (and group swarm participants) to transfer at full speed without relaying through the Exchange."*).
   - **`HAZ-DIRECT-TRANSFER-LISTENER-EXPOSURE` (Transient UDP/ICE Socket Surface for Direct File Transfer & Seeding):**
     - *Cause & Harm:* Allowing a Desktop Persona to open ephemeral UDP/ICE sockets for P2P file transfer and post-download seeding introduces a transient local network attack surface (UDP floods, stale sockets violating `HAZ-PRE-UNLOCK-NETWORK-EXPOSURE`, `HAZ-POST-LOCK-MEMORY-RESIDUE`, and `HAZ-INGRESS-RESOURCE-EXHAUSTION`).
     - *Controls:* Zero listening ports at startup, while idle, or during chat/group/profile traffic (`SYS-PRE-ACTIVATION-STEALTH`); UDP ICE agents are strictly **ephemeral and transfer-scoped**, authenticated via **standard DTLS** (both peers' ephemeral cert SHA-256 fingerprints pinned strictly from Double Ratchet / Group V2 `FileTransferOffer`/`Accept`/`PeerJoin`, zero cert trust on tracker) and bounded by per-session peer/connection caps. Completed downloads may continue seeding **only while the app remains unlocked** (until manually stopped or seeding TTL expires), and all ICE agents, sockets, and tracker registrations tear down atomically upon cancellation, pause, or vault lock (`SYS-LOCK-TEARDOWN-AND-ZEROIZATION`).
   - **Class C SOUP Specification (`soup/datachannel-dotnet.md` per `spec/soup-spec.md`):**
     - `DataChannelDotnet` (`libdatachannel` + `libjuice`, MIT/MPL-2.0) is a native C/C++ network/DTLS transport primitive and MUST be documented as a **Class C SOUP** item, strictly quarantined inside `Percolator.Infrastructure` behind abstract transport ports (zero vendor types or native handles in `Domain`/`Application`/`Apps.FileTransfer`; unused WebRTC media/RTP/SRTP/audio/video surfaces explicitly excluded).
4. **File Transfer System Capabilities & Allocations (`SystemRequirements.md`, `Percolator.Infrastructure/Requirements.md`, `Percolator.Desktop/spec/deployable-requirements.md`, `Percolator.Apps.FileTransfer/Design.md`):**
   - Add **`SYS-FILETRANSFER-DIRECT-DATAPLANE-AND-DISCLOSURE`**: Bulk file transfers execute directly peer-to-peer over ephemeral session-scoped ICE + STUN + DTLS + SCTP DataChannels (with zero mandatory bulk relay traffic, optional operator-opt-in zero-disk encrypted `TransferPipe` fallback for symmetric NATs, and lightweight RAM-only blinded swarm presence tracking + stateless UDP STUN on the Exchange), validate every chunk against standard SHA-256 Merkle trees negotiated over Double Ratchet / Group V2 (`INV-VERIFY-BEFORE-WRITE`), support offline-author group seeding while the app stays unlocked with Group V2 eviction cert pruning, require explicit user IP-disclosure notice before send/accept, and guarantee atomic socket teardown on stop, cancel, or vault lock.
   - Add **`DEP-INFRA-FILETRANSFER-DATAPLANE`**: Allocates the quarantined `DataChannelDotnet` ICE/DTLS/SCTP transport adapter, Exchange blinded `SwarmTrackerService`, embedded stateless RFC 5389 UDP STUN responder, optional operator-opt-in zero-disk `TransferPipe` (TLS 1.3 mTLS passthrough with bounded RAM ring buffer and quotas), and deletes broken `RelayTransferPipeClient` & custom `ChunkCryptoService`.
   - Update **`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`** & **`DEP-DESKTOP-PERSONA-ISOLATION`**: Document the ephemeral transfer/seeding-scoped ICE socket exception (zero always-on listeners; bound only during active user-initiated/accepted transfers or unlocked seeding; atomic teardown on vault lock).

---

### 16.2 Part A — Path 1: Delete Exchange Stranger Discovery & Fix `PreKeyBundleProviderAdapter`

#### Rationale (Privacy-First Architecture)
Academic and protocol audit confirms that stranger discovery on a single untrusted relay forces an unavoidable trade-off (`RoutingToken ↔ IdentityKey`, Sybil scraping, cross-Exchange correlation). Removing Exchange-hosted stranger discovery preserves Percolator's core privacy model: new connections bootstrap strictly via out-of-band `PeerInvitation` (`percolator://invite/...`) or E2E Friend-to-Friend Chat Introductions (`Part C`), with **zero custom discovery crypto**.

#### Ticket `DISC-001` — Upfront QMS Updates for Discovery Retirement
- **Layer / Scope:** QMS Design Inputs (`SystemRequirements.md`, `DecisionRegister.md`, `Percolator.Infrastructure/Requirements.md`) + delete obsolete `Percolator.Apps.Discovery/Design.md` & `Percolator.Apps.Discovery/README.md` in `DISC-002`.
- **Implementation Details:**
  - Retired `SYS-DISCOVERY-PREKEY-ISOLATION` in [`SystemRequirements.md`](file:///C:/Users/squir/source/repos/percolator/source/SystemRequirements.md) and `DEP-INFRA-DISCOVERY-RENDEZVOUS-STORE` in [`Percolator.Infrastructure/Requirements.md`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Requirements.md); updated [`DEP-INFRA-PREKEY-AVAILABILITY`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Requirements.md#L36-L37) to remove `IPreKeyAvailabilityPort` while preserving atomic One-Time PreKey consumption and normalized storage; superseded `DEC-BLINDED-RENDEZVOUS-ROTATION` with `DEC-NO-EXCHANGE-STRANGER-DISCOVERY` in [`DecisionRegister.md`](file:///C:/Users/squir/source/repos/percolator/source/DecisionRegister.md).
  - Run `pwsh -File .gemini/skills/qms-implementation-review-cycle/scripts/validate-qms-compliance.ps1` and get human sign-off prior to `DISC-002`.
- **Acceptance Criteria:**
  - [x] QMS documents updated (`SystemRequirements.md`, `DecisionRegister.md`, `Percolator.Infrastructure/Requirements.md`).
  - [x] QMS compliance checks for `SystemRequirements.md`, `DecisionRegister.md`, and `Percolator.Infrastructure/Requirements.md` pass with 0 errors.

#### Ticket `DISC-002` — Remove `Percolator.Apps.Discovery`, Exchange Discovery Endpoints, Protos & Schema
- **Layer / Scope:** `Percolator.sln`, `Percolator.Domain` (`RelayFeatureFlags`), `Percolator.Apps.Discovery` (delete project), `Percolator.Apps.Discovery.Tests` (delete project), `Percolator.Infrastructure` (`Protos/discovery.proto`, `NearbyDiscoveryEndpoint`, `PrivateRendezvousRelayEndpoint`, `DiscoveryWireFraming`, `DiscoveryQueryClient`, `PreKeyAvailabilityAdapter`, `SqliteRelayPeerDirectory`, `SqlitePrivateRendezvousService`, `RelayDiscoveryRegistrationPruner`, EF Core migration), `README.md`, `Percolator.PluginSdk/README.md`
- **QMS Links:** `DEP-INFRA-VAULT-SCHEMA-MIGRATION`, `SYS-VAULT-UPGRADE-DATA-PRESERVATION`.
- **Implementation Details:**
  - Remove `Percolator.Apps.Discovery` and `Percolator.Apps.Discovery.Tests` projects from `Percolator.sln` and delete their directories cleanly (git history preserves everything).
  - Delete `Percolator.Infrastructure/Protos/discovery.proto`, `NearbyDiscoveryEndpoint.cs`, `PrivateRendezvousRelayEndpoint.cs`, `DiscoveryWireFraming.cs`, `DiscoveryQueryClient.cs`, `PreKeyAvailabilityAdapter.cs`, `SqliteRelayPeerDirectory.cs`, `SqlitePrivateRendezvousService.cs`, `RelayDiscoveryRegistrationPruner.cs`, and all corresponding `Percolator.Infrastructure.Tests` / `Percolator.Simulator` references (note `RelayFeatureFlags.Discovery` removal in `Percolator.Domain/Delivery/Hosting/RelayHostingPolicy.cs`).
  - Remove DI registrations in `InfrastructureServiceCollectionExtensions.cs` and gRPC endpoint mappings in `KestrelPeerTransportServer.cs`.
  - Add an EF Core additive/safe schema migration dropping obsolete `RelayPeerDirectory` and `PrivateRendezvousEntries` tables without disturbing any mailbox/ratchet/contact data (`SYS-VAULT-UPGRADE-DATA-PRESERVATION`).
- **Acceptance Criteria:**
  - [x] Zero references to `Percolator.Apps.Discovery`, `NearbyDiscoveryService`, `PrivateRendezvous`, `PreKeyAvailabilityAdapter`, or `DiscoveryWireFraming` remain anywhere in the solution.
  - [x] All non-Desktop projects (`Percolator.Domain`, `Percolator.Application`, `Percolator.Apps.*`, `Percolator.Infrastructure*`, `Percolator.Simulator*`, and all 12 test suites) build cleanly and pass 100% (Desktop Step 15 relay-only UI refactor remains pending per Step 15).

#### Ticket `DISC-003` — Fix `PreKeyBundleProviderAdapter.FetchConsumableBundleAsync` Bug
- **Layer / Scope:** `Percolator.Application` (`IPreKeyBundleProvider`, `PeerConnectionService`), `Percolator.Infrastructure` (`PreKeyBundleProviderAdapter`)
- **QMS Links:** `DEP-INFRA-PREKEY-AVAILABILITY`, `SYS-CONTACT-CONSENT-GATE`.
- **Implementation Details:**
  - Fix [`PreKeyBundleProviderAdapter.FetchConsumableBundleAsync`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs#L132-L137): currently passes the remote `peerId` as `requesterId` to `FetchAuthenticatedAsync`, guaranteeing `EXCHANGE_NOT_LINKED`.
  - Add explicit `PublicIdentityId requesterId` parameter to `IPreKeyBundleProvider.FetchConsumableBundleAsync` and update [`PeerConnectionService.ConnectToPeerAsync`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Services/PeerConnectionService.cs#L156) to pass `_localIdentityId` as `requesterId`.
  - Verify existing integration/unit tests cover `PeerConnectionService.ConnectToPeerAsync` authenticating with the local caller's mailbox tokens and targeting the remote peer's `RoutingToken`.
- **Acceptance Criteria:**
  - [x] `PreKeyBundleProviderAdapter.FetchConsumableBundleAsync` uses local `requesterId` for `IRelayDirectory.GetMailboxTokensAsync` and remote `routingCapability.RoutingToken` as target.
  - [x] Full non-Desktop solution build and all 12 test suites pass 100% (1,093/1,093 tests; Desktop Step 15 refactor remains pending).

---

### 16.3 Part B — Large File Transfer Data Plane (Option 2: `DataChannelDotnet` ICE/STUN/DTLS/SCTP + Group V2 Swarming + Optional Zero-Disk `TransferPipe`, Zero Custom Crypto)

#### Why Current `Percolator.Apps.FileTransfer` Cannot Execute End-to-End (and Critical Review Findings)
1. **Custom Crypto Landmine (`ChunkCryptoService`):** [`ChunkCryptoService`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/MultiStreamEngine/ChunkCryptoService.cs#L30-L73) rolls deterministic AES-256-GCM encryption (`BaseNonce XOR ChunkIndex`, zero AAD) across a shared swarm `TransferKey`. On-the-fly re-encryption risks catastrophic `(Key, Nonce)` reuse if a chunk varies by 1 byte, and is completely redundant over DTLS/TLS. **Action: Delete `ChunkCryptoService`, `TransferKey`, `BaseNonce`, and custom `SwarmRendezvousId` HMAC-KDF.** Use standard **DTLS** (on direct ICE DataChannels) / **TLS 1.3 mTLS** (on optional `TransferPipe`) + existing standard **SHA-256 Merkle tree** (`TransferManifest`) + random 32-byte `SwarmRendezvousId`.
2. **Broken Group Swarming & Offline-Author Handoff:** [`FileTransferActionHandler`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs#L248) sends `FileTransferOffer` pairwise 1:1 instead of over Group V2 SenderKey, [`FileTransferPayloadHandler`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferPayloadHandler.cs#L168-L195) routes `FileTransferAccept` only to the original initiator (so Bob & Charlie never learn each other's pinned cert fingerprints if Alice goes offline), and [`SwarmCoordinator.CheckSeedRatioCompletionAsync`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/SwarmCoordination/SwarmCoordinator.cs#L126-L139) stops seeding immediately when `targetSeedRatio == 0`.
3. **No WAN NAT Traversal, No Production Runtime, No-Op DI Wiring:** `TcpMultiStreamPool` only dials outbound raw TCP (no WAN hole-punching, no inbound listener, hardcoded port `50001`), `ServiceCollectionExtensions` injects `NullTransferStreamPool.Instance`, `RelayTransferPipeClient` sends plaintext HTTP/1.1 to hardcoded `"https://relay.percolator.net/pipes"`, and `RelaySwarmTrackerClient` is local-SQLite/JSON only.

#### Ticket `FT-000` — Architecture Decision Record (`DEC-FILETRANSFER-DATAPLANE-TRANSPORT`) — **[DECIDED]**
- **Status:** **Approved by Project Owner** (2026-11-25)
- **Decisions Locked:**
  1. **Transport Selection — Option 2 (`DataChannelDotnet`: `libdatachannel` + `libjuice`, MIT/MPL-2.0):**
     - Prebuilt `.NET 10` NuGet binaries (zero Rust FFI toolchain/CI burden), quarantined strictly as **Class C SOUP** (`soup/datachannel-dotnet.md`) inside `Percolator.Infrastructure` behind abstract transport ports (zero vendor types or native handles in `Domain`/`Application`/`Apps.FileTransfer`).
     - Handles **Direct LAN** + **WAN UDP ICE hole-punching (~85–90% direct success, ~55–60 MB/s, \$0 relay bandwidth)** in one standard **ICE + STUN + DTLS + SCTP** stack.
     - Exchange embeds a lightweight stateless **RFC 5389 UDP STUN Binding responder** (~150 LOC, zero disk, zero auth) so peers discover public reflexive `IP:Port` candidates without calling third-party Google/Cloudflare STUN servers.
  2. **Zero Custom Crypto (`INV-STANDARD-CRYPTO-ONLY`):**
     - **Direct path:** Standard **DTLS** encryption inside `libdatachannel`, with each peer's 32-byte SHA-256 cert fingerprint pinned **exclusively via Double Ratchet / Group V2** (`FileTransferOffer`, `FileTransferAccept`, `FileTransferPeerJoin`). **Never trust `SwarmTrackerService` with cert fingerprints.**
     - **ICE signaling privacy:** SDP Offer/Answer and trickle ICE candidates travel **E2E encrypted** over Double Ratchet (1:1) or Group V2 SenderKey (group)—zero custom signaling crypto, and the Exchange never sees peers' LAN IPs or SDP `ice-ufrag`/`ice-pwd`.
     - **Chunk integrity:** Standard **SHA-256 Merkle tree** over chunk hashes inside `TransferManifest` delivered over Double Ratchet / Group V2 (`INV-VERIFY-BEFORE-WRITE`). Delete `ChunkCryptoService`, `TransferKey`, `BaseNonce`, and HMAC-KDF `SwarmRendezvousId`.
  3. **Two Independent Zero-Disk Exchange Roles (`RelayFeatureFlags`):**
     - **`RelayFeatureFlags.SwarmTracker`:** RAM-only presence directory keyed by opaque random 32-byte `SwarmRendezvousId` → active `EphemeralSwarmPeerId`s with TTL & quota caps (**0 file bytes, 0 disk persistence, 0 Persona IDs, 0 cert hashes**), paired with the stateless RFC 5389 UDP STUN responder.
     - **`RelayFeatureFlags.TransferPipe` (OFF by default — operator opt-in):** Zero-disk RAM-ring-buffered pass-through pipe (~256 KB bounded buffer per pipe with backpressure; configurable `MaxConcurrentPipes`, `MaxBytesPerSecondPerPipe`, `MaxBytesPerSession`, `PipeIdleTimeout`) used *only* as a fallback when direct ICE fails (e.g., dual symmetric NATs). Carries standard `.NET` **`SslStream` TLS 1.3 mTLS** ciphertext end-to-end using the Double-Ratchet/Group-V2-pinned cert fingerprints (**0 bytes written to Exchange disk**, Exchange sees ciphertext only). Delete broken `RelayTransferPipeClient` & `"https://relay.percolator.net/pipes"`.
  4. **Group V2 Swarming & Eviction Safety:**
     - Broadcast `FileTransferOffer` and `FileTransferPeerJoin(sessionId, ephemeralSwarmPeerId, certSha256)` over the **Group V2 SenderKey channel** so every participating member learns every other member's pinned cert fingerprint directly and swarms survive when the original author goes offline.
     - Subscribe to Group V2 `MemberLeft` (`Epoch E -> E+1`): immediately revoke the evicted member's pinned cert fingerprint and tear down any active DataChannel/pipe to that peer.
  5. **Offline-Author Seeding While Unlocked:**
     - Completed downloads transition to `Seeding` and **keep seeding while the app stays unlocked** (until manually stopped or configurable seeding TTL expires), replacing `SwarmCoordinator`'s instant shutdown on `targetSeedRatio == 0`.
     - Persist each active transfer's ephemeral DTLS/TLS keypair in the encrypted SQLCipher vault for the transfer's lifetime (zeroizing/deleting upon transfer removal) so restarts/resumes retain the pinned cert fingerprint.
     - **Immediate Lock Teardown:** Vault lock or app exit atomically closes all ICE agents, UDP sockets, optional `TransferPipe` streams, and unregisters from `SwarmTrackerService` (`SYS-LOCK-TEARDOWN-AND-ZEROIZATION`).
  6. **Pure-Client Stealth & Explicit IP Disclosure (`INV-EPHEMERAL-SESSION-LISTENER`, `HAZ-DIRECT-TRANSFER-IP-DISCLOSURE`):**
     - **0 listening sockets** at startup, while idle, and during all chat/group/profile traffic (`SYS-PRE-ACTIVATION-STEALTH`).
     - Explicit, unavoidable IP-disclosure notice displayed before Send and Accept, restricted strictly to approved contacts / authorized Group V2 swarm peers.

---

#### Ticket `FT-001` — Upfront QMS Updates & Class C SOUP Spec for Direct P2P File Transfer
- **Layer / Scope:** QMS Design Inputs (`RiskRegister.md`, `SystemRequirements.md`, `Percolator.Infrastructure/Requirements.md`, `Percolator.Desktop/spec/deployable-requirements.md`, `Percolator.Apps.FileTransfer/Design.md`, new `soup/datachannel-dotnet.md`)
- **Dependencies:** `FT-000` (Approved)
- **Implementation Details:**
  - Create **`soup/datachannel-dotnet.md`** conforming to [`spec/soup-spec.md`](file:///C:/Users/squir/source/repos/percolator/source/spec/soup-spec.md) (Class C SOUP identification, exact NuGet version & hash, native prerequisites, functional envelope restricting usage strictly to ICE/STUN/DTLS/SCTP DataChannels and excluding media/RTP/SRTP, architectural quarantine rules, and reachable anomaly assessment).
  - Add `HAZ-DIRECT-TRANSFER-IP-DISCLOSURE` and `HAZ-DIRECT-TRANSFER-LISTENER-EXPOSURE` to `RiskRegister.md`.
  - Add `SYS-FILETRANSFER-DIRECT-DATAPLANE-AND-DISCLOSURE` to `SystemRequirements.md` and `DEP-INFRA-FILETRANSFER-DATAPLANE` to `Percolator.Infrastructure/Requirements.md`.
  - Update `Percolator.Desktop/spec/deployable-requirements.md` (`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`, `DEP-DESKTOP-PERSONA-ISOLATION`) for ephemeral transfer/seeding-scoped ICE sockets with immediate vault-lock teardown.
  - Update `Percolator.Apps.FileTransfer/Design.md` with `DEC-FILETRANSFER-DATAPLANE-TRANSPORT` (Option 2 `DataChannelDotnet`, Group V2 `FileTransferOffer`/`PeerJoin` + eviction pruning, offline-author seeding while unlocked, optional zero-disk `TransferPipe`, removing `ChunkCryptoService`) and `INV-EPHEMERAL-SESSION-LISTENER`.
  - Verify `pwsh -File scripts/validate-qms-compliance.ps1` passes and obtain human sign-off prior to `FT-002`.
- **Acceptance Criteria:**
  - [x] `soup/datachannel-dotnet.md` and all QMS documents updated and approved prior to `FT-002`.
  - [x] `validate-qms-compliance.ps1` passes with 0 errors across all normative QMS documents (247 unique semantic IDs, 0 duplicates).

#### Ticket `FT-002` — Exchange Blinded `SwarmTrackerService`, Embedded RFC 5389 UDP STUN, Optional Zero-Disk `TransferPipe` & Cleanup
- **Layer / Scope:** `Percolator.Domain` (`RelayFeatureFlags`, `RelayHostingPolicy`), `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure` (`Protos/file_transfer_tracker.proto`, `ExchangeSwarmTrackerEndpoint`, `ExchangeTransferPipeEndpoint`, `UdpStunResponderService`, `GrpcSwarmTrackerClient`, `KestrelPeerTransportServer`)
- **Dependencies:** `FT-001`
- **QMS Links:** `SYS-STREAM-AEAD-CHUNKED-TRANSFER`, `SYS-BOUNDED-RELAY-QUOTAS`, `DEP-INFRA-FILETRANSFER-DATAPLANE`.
- **Implementation Details:**
  - **Delete Dead Pipe & Custom Chunk Crypto:**
    - Delete `Percolator.Infrastructure/Plugins/FileTransfer/RelayTransferPipeClient.cs`, remove `"https://relay.percolator.net/pipes"` from `NetworkInterfaceEndpointDiscoveryPort.cs`, and delete legacy hardcoded-port/dead TCP pipe code.
    - Delete `ChunkCryptoService.cs`, `TransferKey`, `BaseNonce`, and HMAC-KDF `SwarmRendezvousId.Derive(TransferKey)`. Replace `SwarmRendezvousId` with `RandomNumberGenerator.GetBytes(32)` negotiated in `FileTransferOfferPayload`. Update `ChunkDataFrame` to carry plaintext chunk bytes over the DTLS / TLS 1.3 mTLS channel (verified directly against `TransferManifest` SHA-256 Merkle leaf).
  - **Domain Policy (`RelayHostingPolicy`):**
    - Add `RelayFeatureFlags.SwarmTracker` and `RelayFeatureFlags.TransferPipe` (**OFF by default** in `RelayHostingPolicy.Default` and `HostManaged`; operator opt-in).
    - Add bounded quotas (`MaxActiveSwarms`, `MaxPeersPerSwarm`, `SwarmPeerEntryTtl` default 5 min; `MaxConcurrentPipes`, `MaxBytesPerSecondPerPipe`, `MaxBytesPerSession`, `PipeIdleTimeout`) (`SYS-BOUNDED-RELAY-QUOTAS`).
  - **Pure Binary Protobuf (`file_transfer_tracker.proto` — zero JSON, zero disk, zero crypto trust):**
    - `SwarmTrackerService` over `ChannelPoolKind.Unauthenticated`:
      - `Announce(SwarmAnnounceRequestProto) returns (SwarmAnnounceResponseProto)`: keyed strictly by opaque random 32-byte `SwarmRendezvousId`, carries random session-scoped `EphemeralSwarmPeerId` (NOT `PublicIdentityId`), returns active online `EphemeralSwarmPeerId`s + Exchange STUN UDP endpoint metadata (**ZERO Persona IDs, ZERO cert hashes, ZERO disk persistence**).
      - `Unregister(SwarmUnregisterRequestProto) returns (SwarmUnregisterResponseProto)`: cleans up on stop, cancel, pause, or vault lock.
    - `TransferPipeService` (gated on `RelayFeatureFlags.TransferPipe`):
      - `OpenPipe(stream PipeChunkProto) returns (stream PipeChunkProto)`: pairs two callers presenting matching random one-time 32-byte `PipeRendezvousToken` (exchanged E2E over Double Ratchet / Group V2), forwards opaque ciphertext through a bounded ~256 KB RAM ring buffer with backpressure, enforces concurrency / bytes-per-second / max-bytes-per-session / idle-timeout quotas, and writes **0 bytes to disk**.
  - **Embedded RFC 5389 UDP STUN Responder (`UdpStunResponderService`):**
    - Lightweight stateless UDP listener (~150 LOC) answering RFC 5389 STUN Binding Requests with `XOR-MAPPED-ADDRESS` when `RelayFeatureFlags.SwarmTracker` is enabled on the Exchange (zero disk, zero state, strict packet size & rate bounds).
  - **`GrpcSwarmTrackerClient`:** Replaces local-SQLite/JSON `RelaySwarmTrackerClient` (zero `System.Text.Json`) and resolves Exchange URI from `IRelayDirectory`.
- **Acceptance Criteria:**
  - [x] Zero references to `ChunkCryptoService`, `RelayTransferPipeClient`, or `"https://relay.percolator.net/pipes"` remain.
  - [x] `SwarmTrackerService` and `TransferPipeService` store zero Persona IDs, zero cert hashes, and write **0 bytes to disk**; `TransferPipe` is disabled by default unless opted in by the Exchange operator.
  - [x] Embedded RFC 5389 UDP STUN responder returns valid `XOR-MAPPED-ADDRESS` responses on Binding Requests.

#### Ticket `FT-003` — Quarantined `DataChannelDotnet` ICE/STUN/DTLS/SCTP Transport Adapter & Optional `TransferPipe` TLS 1.3 mTLS Fallback
- **Layer / Scope:** `Percolator.Apps.FileTransfer` (transport ports & wire frames), `Percolator.Infrastructure/Plugins/FileTransfer` (`WebRtc/*` quarantined `DataChannelDotnet` adapter, `TransferPipeTlsStreamTransport`, SQLCipher ephemeral cert persistence)
- **Dependencies:** `FT-002`
- **QMS Links:** `SYS-PRE-ACTIVATION-STEALTH`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`, `SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`, `HAZ-DIRECT-TRANSFER-IP-DISCLOSURE`, `HAZ-DIRECT-TRANSFER-LISTENER-EXPOSURE`, `SOUP-DATACHANNEL-DOTNET`.
- **Implementation Details:**
  - **SOUP Quarantine (`Percolator.Infrastructure/Plugins/FileTransfer/WebRtc/`):**
    - Add pinned `DataChannelDotnet` NuGet package strictly inside `Percolator.Infrastructure` (never referenced by `Domain`, `Application`, or `Apps.FileTransfer`).
    - Expose abstract `ITransferPeerTransport` / `ITransferDataChannel` ports; zero `DataChannelDotnet` types, callbacks, or native handles cross the adapter boundary.
    - Configure `libjuice` ICE agents with the Exchange's embedded UDP STUN server (no hardcoded external third-party STUN servers).
  - **E2E Cert Pinning & Vault-Persisted Ephemeral Session Cert:**
    - Generate a transfer-scoped ephemeral ECDSA P-256 certificate/keypair and persist it in the encrypted SQLCipher vault for the transfer's lifetime (zeroizing/deleting on final transfer removal) so offline-author seeding and pause/resume preserve the pinned SHA-256 fingerprint.
    - Carry each participant's 32-byte `CertSha256Fingerprint` strictly inside E2E Double Ratchet / Group V2 payloads (`FileTransferOfferPayload`, `FileTransferAcceptPayload`, `FileTransferPeerJoinPayload`).
    - Exchange SDP Offer/Answer and trickle ICE candidates **E2E** via `FileTransferIceSignalPayload` over Double Ratchet (1:1) or Group V2 SenderKey (group).
    - Enforce strict **DTLS certificate fingerprint verification** against the E2E-pinned fingerprint map; reject mismatches and enforce a connection handshake deadline + per-session peer cap.
  - **SCTP Backpressure & Pooled Framing (`INV-POOLED-STREAM-BUFFERS`):**
    - Use `ArrayPool<byte>` framing over reliable ordered/unordered SCTP DataChannels respecting `bufferedAmount` / `bufferedAmountLowThreshold` so multi-GB transfers maintain bounded RAM without exhausting memory.
    - Require a valid `HandshakeFrame` matching `(TransferSessionId, StreamAuthToken)` via `CryptographicOperations.FixedTimeEquals` upon DataChannel open.
  - **Optional Zero-Disk `TransferPipe` Fallback (Standard TLS 1.3 mTLS):**
    - If direct ICE negotiation times out/fails (e.g., dual symmetric NATs):
      - If the Exchange advertises `RelayFeatureFlags.TransferPipe`, exchange a random one-time `PipeRendezvousToken` over the E2E channel, open `TransferPipeService.OpenPipe`, and wrap the stream in standard `.NET` **`SslStream` TLS 1.3 mTLS** pinning the exact same E2E-verified SHA-256 cert fingerprints on both client and server.
      - Otherwise fail cleanly with `DIRECT_NAT_TRAVERSAL_FAILED`.
- **Acceptance Criteria:**
  - [x] Zero listening UDP/TCP sockets exist before transfer start or after stop/pause/cancel/lock.
  - [x] Every DataChannel (DTLS) and optional `TransferPipe` stream (TLS 1.3 mTLS) enforces E2E-pinned SHA-256 cert fingerprints on both peers.
  - [x] Zero `DataChannelDotnet` types leak outside `Percolator.Infrastructure`.
  - [x] Hardcoded port `50001` is completely eliminated.

#### Ticket `FT-004` — Session-Scoped `ITransferSessionRuntime`, Group V2 Swarming, Offline-Author Seeding & DI Fix
- **Layer / Scope:** `Percolator.Apps.FileTransfer` (`ServiceCollectionExtensions`, `FileTransferActionHandler`, `FileTransferPayloadHandler`, `SwarmCoordinator`, new `ITransferSessionRuntime` / `TransferSessionRuntime`)
- **Dependencies:** `FT-003`
- **QMS Links:** `SYS-STREAM-AEAD-CHUNKED-TRANSFER`, `SYS-LOCK-TEARDOWN-AND-ZEROIZATION`, `INV-VERIFY-BEFORE-WRITE`, `INV-POOLED-STREAM-BUFFERS`.
- **Implementation Details:**
  - Remove `NullTransferStreamPool.Instance` from `Percolator.Apps.FileTransfer/DependencyInjection/ServiceCollectionExtensions.cs`.
  - **Group V2 Swarming & Eviction Pruning:**
    - Update [`FileTransferActionHandler`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs#L248) to broadcast `FileTransferOffer` over the **Group V2 SenderKey channel** for group conversations (1:1 continues over Double Ratchet).
    - Update [`FileTransferPayloadHandler`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferPayloadHandler.cs#L168-L195) so accepting a group transfer broadcasts `FileTransferPeerJoin(sessionId, ephemeralSwarmPeerId, certSha256)` over Group V2 SenderKey—allowing every group peer (e.g., Bob & Charlie) to pin each other's cert fingerprints directly and swarm even when Alice goes offline.
    - Subscribe to Group V2 `MemberLeft` (`Epoch E -> E+1`): atomically remove the evicted peer's pinned cert fingerprint from all active group transfers and drop their DataChannels/pipes.
  - **Offline-Author Seeding While Unlocked:**
    - Update [`SwarmCoordinator`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/SwarmCoordination/SwarmCoordinator.cs#L126-L139) so completed downloads transition to `Seeding` and **remain seeding while the app stays unlocked** (until manually stopped by the user or configurable seeding TTL expires), instead of shutting down immediately when `targetSeedRatio == 0`.
  - **Persona-Scoped `ITransferSessionRuntime` (`IAsyncDisposable` — disposed atomically on vault lock):**
    - Owns `TransferSessionId -> ActiveTransferHandle` (transport/ICE agents, `TransferSessionCoordinator`, `SwarmCoordinator`, `CancellationTokenSource`, background pump `Task`, tracker heartbeat `Task`).
    - Coordinates rarest-first piece selection across connected peers, verifies every received chunk against `TransferManifest` SHA-256 Merkle leaf **before writing** (`INV-VERIFY-BEFORE-WRITE`), and atomically finalizes `.percolator-part` files via `IDesktopFileStorage`.
    - **Lifecycle & Teardown:** Pause, Cancel, Manual Stop Seeding, and Vault Lock cancel session CTS, tear down all ICE agents / DataChannels / `TransferPipe` streams within 1s, and call `SwarmTrackerService.Unregister`.
- **Acceptance Criteria:**
  - [x] `NullTransferStreamPool` is removed from production DI wiring.
  - [x] Group V2 `FileTransferOffer` + `FileTransferPeerJoin` + `MemberLeft` cert revocation run end-to-end.
  - [x] Completed downloads keep seeding while unlocked and survive the original author going offline; vault lock tears down all sockets/ICE agents within 1s and unregisters from tracker.

#### Ticket `FT-005` — End-to-End Multi-Node File Transfer, Offline-Author Swarm & Fallback Integration Tests
- **Layer / Scope:** `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.ApplicationIntegrationTests`
- **Dependencies:** `FT-004`
- **QMS Links:** `[Verify: Simulation]` & `[Verify: Integration]` for `SYS-STREAM-AEAD-CHUNKED-TRANSFER`, `SYS-PATH-TRAVERSAL-DEFENSE`, `SYS-FILETRANSFER-DIRECT-DATAPLANE-AND-DISCLOSURE`.
- **Test Plan (Real `DataChannelDotnet` ICE/DTLS/SCTP + Real Exchange `SwarmTrackerService` / UDP STUN / `TransferPipe`, Zero Disk Peeking):**
  - **1:1 Direct DataChannel Round-Trip:** Alice & Bob transfer a 2 MB multi-file bundle over real `DataChannelDotnet` ICE + DTLS + SCTP using Exchange UDP STUN; verify byte-for-byte SHA-256 match and assert 0 open transfer sockets before start and immediately after stop/teardown.
  - **3-Node Group V2 Swarm & Offline-Author Seeding Test:** Alice offers to Group V2 → Bob downloads 100% and stays seeding while unlocked → **Alice goes offline** → Charlie joins via Group V2 `FileTransferPeerJoin` + `SwarmTrackerService` → Charlie pins Bob's DTLS fingerprint and downloads 100% directly from Bob → all three match SHA-256.
  - **Group Eviction Test:** Evict Charlie (`Epoch E -> E+1`) → assert Bob immediately revokes Charlie's pinned cert fingerprint and rejects further chunk requests.
  - **Optional Zero-Disk `TransferPipe` Fallback & Quota Test:** Force direct ICE failure with `RelayFeatureFlags.TransferPipe` enabled → verify transfer succeeds end-to-end over TLS 1.3 mTLS pipe with **0 bytes written to Exchange disk** and respects concurrency/rate/total-byte caps; verify clean `DIRECT_NAT_TRAVERSAL_FAILED` when `TransferPipe` is disabled (default).
  - **Vault Lock Teardown & Resume Test:** Trigger Persona lock mid-transfer and while seeding → verify ICE agents/sockets close within 1s and tracker unregisters; unlock and resume skips already-verified chunks via `ResumptionBitfield` using the vault-persisted session cert.
  - **Integrity & Security Tests:** Tampered chunk fails SHA-256 Merkle verification, chokes offending peer, writes 0 bytes to disk (`INV-VERIFY-BEFORE-WRITE`); spoofed/mismatched DTLS or TLS cert fingerprint fails handshake immediately; path traversal manifest (`../escape`) rejected (`SYS-PATH-TRAVERSAL-DEFENSE`).
- **Acceptance Criteria:**
  - [x] All unit and real-transport integration tests pass 100% with zero cross-host peeking or fake listeners.

---

### 16.4 Part C — E2E Friend-to-Friend Chat Introductions (`INTRO-*`) & Downstream Enablement (`FT-006`)

#### Architectural Design: Ask-Before-Introducing (Option A — Zero Profile Bloat, Zero New Crypto)
Peer introductions live inside **`Percolator.Apps.Chat`** as a standard E2E Double Ratchet workflow—leaving [`ProfileMetadata`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.PluginSdk/Profiles/IProfilePorts.cs#L90-L95) strictly as identity presentation (`DisplayName`, `Avatar`, `StatusBio`, `Revision`):
1. **Consent Ask (`Alice → Bob` over existing 1:1 Double Ratchet):** Alice selects *"Introduce Bob & Charlie"* → Alice sends `PeerIntroductionConsentRequest` to Bob naming Charlie (`CharliePeerId`, `CharlieDisplayName` if known, optional note) **without** sending Charlie's `PeerInvitation` yet (or vice versa).
2. **Per-Introduction Consent (`Bob`):** Bob chooses **Allow** (generates/returns Bob's `PeerInvitation` payload over Double Ratchet to Alice) or **Decline** (Alice receives refusal; Charlie never sees Bob's contact card).
3. **E2E Forward & TOFU Gate (`Alice → Charlie` → `Bob`):** Upon consent, Alice forwards the approved `PeerIntroductionCard` over her existing Double Ratchet channel with Charlie → Charlie clicks **Connect** → normal `OutboundConnectionWorkflow.ConnectAsync` → Bob receives inbound handshake in `PendingApproval` (`SYS-CONTACT-CONSENT-GATE`). **Zero Exchange visibility, zero scraping, zero changes to `ProfileMetadata`.**

#### Ticket `INTRO-001` — QMS Updates for Chat Peer Introductions `[COMPLETED]`
- **Status:** Complete (`RiskRegister.md`, `SystemRequirements.md`, `Percolator.Apps.Chat/Design.md` updated; `validate-qms-compliance.ps1` passing 100%)
- **Layer / Scope:** `RiskRegister.md`, `Percolator.Apps.Chat/Design.md`
- **Dependencies:** `FT-005` (or runnable immediately after `DISC-003` if prioritized ahead of UI)
- **Implementation Details:** Document `HAZ-INTRO-UNSOLICITED-FORWARD` mitigation (Option A Ask-Before-Introducing + `SYS-CONTACT-CONSENT-GATE`) and run `scripts/validate-qms-compliance.ps1`.

#### Ticket `INTRO-002` — Chat E2E Introduction Payloads, Workflow & SQLite Persistence `[COMPLETED]`
- **Status:** Complete (Protobuf wire payloads opcodes 11-13 with `SteppedPadding`, domain aggregate `PeerIntroductionConsent`, `IPeerIntroductionConsentRepository` with SQLite EF Core mapping, application services in `IChatService`/`ChatService`, ingress validation in `ChatPayloadHandler`, 137 unit tests passing, 337 infrastructure tests passing, 32 integration tests passing).
- **Layer / Scope:** `Percolator.Apps.Chat` (`Features/Introductions/*`, `ChatPayloadType`, `ChatPayloadHandler`, `IChatService`), `Percolator.Infrastructure` (`PeerIntroductionConsentEntity` + EF mapping)
- **Dependencies:** `INTRO-001`
- **Implementation Details:**
  - Add pure Protobuf-serialized `ChatPayloadType` opcodes (`IntroductionConsentRequest`, `IntroductionConsentResponse`, `PeerIntroductionCard`) inside `Percolator.Apps.Chat` (zero JSON on wire, padded via `SteppedPadding`).
  - Enforce same-Exchange scoping (`ChannelId.DeterministicForDirect`) or explicit Exchange endpoint matching on forwarded invitations, plus blocked/duplicate contact checks (`SYS-KEY-CHANGE-TRUST-HALT`).
  - Persist pending outbound/inbound introduction consent state in SQLite so consent requests survive app restart.

#### Ticket `INTRO-003` — Multi-Node Chat Introduction Integration Tests `[COMPLETED]`
- **Status:** Complete (Multi-node integration test scenarios in `ChatIntroductionIntegrationTests.cs` exercising real loopback Exchange, X3DH handshakes, and E2E Double Ratchet messaging: (a) Ask-Before-Introducing allowed flow with auto-forward and manual forward, (b) Declined consent with zero cards transmitted and zero connection attempts, and (c) SQLite persistence verification across all nodes).
- **Layer / Scope:** `Percolator.Apps.Chat.Tests`, `Percolator.ApplicationIntegrationTests`
- **Dependencies:** `INTRO-002`
- **Test Plan:** Real loopback Exchange tests covering (a) Alice asks Bob to introduce Charlie → Bob Allows → Alice forwards → Charlie connects → bidirectional Double Ratchet chat succeeds with zero Exchange discovery endpoints; and (b) Bob Declines → zero contact card transmitted and zero connection attempt occurs.

#### Ticket `FT-006` — Downstream Simulator (`08-swarm-file-transfer.json`) & Desktop UX Enablement `[COMPLETED]`
- **Status:** Complete (Simulator rebuilt `08-swarm-file-transfer.json` on real persona hosts passing 5/5 assertions and zero errors; Persona Tab 1 QA triggers for E2E introductions enabled; Desktop UI enabled with unavoidable IP-disclosure warning modal before send/accept, selective file picker, active seeding indicator with `[Stop Seeding]`, swarm progress & pause/resume/cancel, clear NAT traversal failure feedback, automatic vault-lock teardown, and 1:1 "Introduce Contact..." action with Ask-Before-Introducing consent dialog).
- **Layer / Scope:** `Percolator.Simulator`, `Percolator.Desktop`, `Percolator.Desktop.Tests`
- **Dependencies:** `FT-005` (and `INTRO-003` for Desktop/Simulator Introduction UX)
- **Implementation Details:**
  - Rebuilt `08-swarm-file-transfer.json` cleanly in `Percolator.Simulator` on top of real persona hosts (zero peeking, zero fake coordinator sockets) exercising multi-stream file transfers, 50% late joiner injection, and SHA-256 Merkle leaf verification.
  - Added QA triggers in `Percolator.Simulator` for E2E Peer Introductions inside Persona Tab 1 (`🤝 Introduce Contact ▼` flyout + `Pending Introduction Consent Banner` with Allow/Decline).
  - Desktop UI (`Percolator.Desktop`):
    - Added File Transfer Send/Accept flow inside approved 1:1 and Group conversations with the unavoidable **IP-disclosure notice** (*"Direct P2P file transfer connects directly to `<Contact>` (and group swarm peers) without relaying through the Exchange. This reveals your IP address to participating peers."*), platform file/directory picker abstraction (`IFilePickerService`), swarm progress display, active seeding indicator with `[Stop Seeding]`, pause/resume/cancel, clear NAT-traversal failure feedback, and automatic vault-lock teardown.
    - Added *"Introduce Contact..."* menu action inside active 1:1 chat with candidate contact selection and Ask-Before-Introducing inbound consent banner.
- **Verification Gate:**
  - `dotnet test Percolator.Desktop.Tests/Percolator.Desktop.Tests.csproj` (250/250 tests passed, including dedicated `ChatViewModelFileTransferAndIntroTests`).
  - `dotnet test Percolator.Simulator.Tests/Percolator.Simulator.Tests.csproj` (30/30 tests passed).
  - `dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj --framework net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/08-swarm-file-transfer.json` (all 4 steps and 5 assertions passed).
  - `validate-qms-compliance.ps1` & `verify-code-integrity.ps1` passed 100%.

---

### 16.5 Part E — File Transfer DDD, Hexagonal Architecture & High-Throughput Hardening (`FT-007` to `FT-012`)

#### Ticket `FT-007` — Normalized SQLite Relational Tables, Bulk Persistence & Dedicated `ITransferSessionTransportStore` Port
- **Layer / Scope:** `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Infrastructure.Tests`, `Percolator.Apps.FileTransfer.Tests`
- **Dependencies:** `FT-006`
- **Architectural Defects Addressed:**
  - `ITransferSessionRepository` leaks non-aggregate transport columns onto `ft_transfer_sessions`, declares a `private static readonly ConcurrentDictionary` fallback + Default Interface Methods inside the port interface, and forces `SqliteTransferSessionRepository` to maintain an in-memory `_transportContexts` cache.
  - Five relational collections are stored as JSON columns (`TransferSessionEntity.RecipientsJson`, `SelectedFilesJson`, `DirectCandidatesJson`; `FilterPolicyEntity.BlockedPeersJson`, `BlockedRelaysJson`).
  - `SqliteTransferSessionRepository.GetActiveSessionsAsync` executes an N+1 query loop per session, contains an unused orphan `SaveBitfieldAsync` method, and inserts `ft_manifest_files` one EF-tracked entity at a time (stalls for ~15 s on 50k-file bundles like `node_modules`).
  - `TransferOfferReaperService` calls `GetActiveSessionsAsync()` on every sweep, loading 50k `ManifestFileEntity` rows per active session just to check `PendingConsent` expiration timestamps.
  - `SqlCipherPragmaInterceptor.StandardWalPragmas` omits `PRAGMA busy_timeout`, causing immediate `SQLITE_BUSY` failures when multiple concurrent transfers checkpoint alongside chat/outbox writes.
  - `SqliteDatabaseMigrator` contains two broken PascalCase file-transfer schema-upgrade DDL blocks that mismatch `PercolatorDbContext`'s `ToSnakeCase` convention: `ApplyFileTransferSchemaUpgradesAsync` (lines 71–120, adding PascalCase transport columns to `ft_transfer_sessions`) and lines 193–201 inside `ApplyPreKeySchemaUpgradesAsync` (creating `"ft_session_certificates"` with PascalCase columns).
  - **Schema Policy (Zero EF Migrations):** There are zero live clients; the SQLite database file will be deleted before running the app. `FT-007`–`FT-012` MUST introduce **zero EF Core migrations** and **zero manual `ALTER TABLE` / `CREATE TABLE` upgrade DDL** — fresh schema creation relies exclusively on `PercolatorDbContext` + `Database.GenerateCreateScript()`.
- **Exact Implementation Specification:**
  1. **Create `Percolator.Apps.FileTransfer/Ports/ITransferSessionTransportStore.cs`:**
     ```csharp
     using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
     using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

     namespace Percolator.Apps.FileTransfer.Ports;

     public interface ITransferSessionTransportStore
     {
         ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default);
         ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default);
         ValueTask DeleteTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default);
     }
     ```
  2. **Clean `Percolator.Apps.FileTransfer/Ports/ITransferSessionRepository.cs` & Optimize `TransferOfferReaperService`:**
     - Delete `using System.Collections.Concurrent;`, `using Percolator.Apps.FileTransfer.Features.TransferNegotiation;`, `FallbackContexts`, `SaveTransportContextAsync`, and `GetTransportContextAsync`. Retain/declare:
       - `ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default);`
       - `ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);`
       - `ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default);`
       - `ValueTask<IReadOnlyList<TransferSessionId>> GetExpiredPendingOfferIdsAsync(DateTimeOffset createdBeforeUtc, CancellationToken ct = default);`
     - In `Percolator.Apps.FileTransfer/Features/TransferNegotiation/TransferOfferReaperService.cs`: query `GetExpiredPendingOfferIdsAsync(_clock.UtcNow - retentionWindow, ct)` and only load & expire matching sessions via `GetSessionByIdAsync` + `ExpireOffer` + `SaveSessionAsync`, avoiding loading 50k-row manifests for active non-expired transfers.
  3. **Normalize `Percolator.Infrastructure/Persistence/Entities/FileTransferEntities.cs`:**
     - `TransferSessionEntity` (aggregate table `ft_transfer_sessions` — remove all 11 transport columns `ChannelId`, `LocalIdentityId`, `LocalRootPath`, `WanPolicy`, `SwarmRendezvousId`, `EphemeralPeerId`, `StreamAuthToken`, `RelayEndpointUri`, `RelayPipeToken`, `IsGroupTransfer`, `SenderCertSha256` AND remove `RecipientsJson`, `SelectedFilesJson`, `DirectCandidatesJson`; add `bool AllFilesSelected` so full-bundle transfers store **0 rows** in `ft_session_selected_files`):
       ```csharp
       public sealed class TransferSessionEntity
       {
           public Guid TransferId { get; set; }
           public byte[] InitiatorId { get; set; } = Array.Empty<byte>();
           public int TopologyType { get; set; }
           public int Role { get; set; }
           public int State { get; set; }
           public string RootName { get; set; } = string.Empty;
           public long TotalSizeBytes { get; set; }
           public int ChunkSizeBytes { get; set; }
           public byte[] MerkleRoot { get; set; } = Array.Empty<byte>();
           public byte[] ConcatenatedLeaves { get; set; } = Array.Empty<byte>();
           public bool AllFilesSelected { get; set; }
           public double TargetSeedRatio { get; set; }
           public long UploadedSizeBytes { get; set; }
           public DateTimeOffset CreatedAtUtc { get; set; }
           public DateTimeOffset? CompletedAtUtc { get; set; }
       }
       ```
     - `FilterPolicyEntity` (remove `BlockedPeersJson` and `BlockedRelaysJson`):
       ```csharp
       public sealed class FilterPolicyEntity
       {
           public byte[] OwnerId { get; set; } = Array.Empty<byte>();
           public DateTimeOffset UpdatedAtUtc { get; set; }
       }
       ```
     - Add dedicated transport context entity and 5 normalized child entities:
       ```csharp
       public sealed class TransferSessionRecipientEntity
       {
           public Guid TransferId { get; set; }
           public byte[] RecipientIdentityId { get; set; } = Array.Empty<byte>();
       }

       public sealed class TransferSessionSelectedFileEntity
       {
           public Guid TransferId { get; set; }
           public int FileIndex { get; set; }
       }

       public sealed class TransferSessionTransportContextEntity
       {
           public Guid TransferId { get; set; }
           public Guid ChannelId { get; set; }
           public byte[] LocalIdentityId { get; set; } = Array.Empty<byte>();
           public byte[] SwarmRendezvousId { get; set; } = Array.Empty<byte>();
           public byte[] EphemeralPeerId { get; set; } = Array.Empty<byte>();
           public byte[] StreamAuthToken { get; set; } = Array.Empty<byte>();
           public int WanPolicy { get; set; }
           public string? LocalRootPath { get; set; }
           public string? RelayEndpointUri { get; set; }
           public byte[]? RelayPipeToken { get; set; }
           public bool IsGroupTransfer { get; set; }
           public byte[]? SenderCertSha256 { get; set; }
       }

       public sealed class TransferSessionDirectCandidateEntity
       {
           public Guid TransferId { get; set; }
           public string Host { get; set; } = string.Empty;
           public int Port { get; set; }
           public int Kind { get; set; }
       }

       public sealed class FilterPolicyBlockedPeerEntity
       {
           public byte[] OwnerId { get; set; } = Array.Empty<byte>();
           public byte[] BlockedPeerId { get; set; } = Array.Empty<byte>();
       }

       public sealed class FilterPolicyBlockedRelayEntity
       {
           public byte[] OwnerId { get; set; } = Array.Empty<byte>();
           public string RelayEndpointUri { get; set; } = string.Empty;
       }
       ```
  4. **Configure `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs` & `SqlCipherPragmaInterceptor.cs`:**
     - In `Percolator.Infrastructure/Vault/SqlCipherPragmaInterceptor.cs` (line 14): append `PRAGMA busy_timeout = 5000;` to `StandardWalPragmas` so concurrent transfer checkpoints + chat/outbox writes wait cleanly instead of failing with immediate `SQLITE_BUSY`.
     - Add `DbSet` properties in `PercolatorDbContext`:
       - `TransferSessionRecipients`, `TransferSessionSelectedFiles`, `TransferSessionTransportContexts`, `TransferSessionDirectCandidates`, `FilterPolicyBlockedPeers`, `FilterPolicyBlockedRelays`.
     - Update `TransferSessionEntity` mapping (remove BLOB configurations for columns moved to `TransferSessionTransportContextEntity`; keep `InitiatorId`, `MerkleRoot`, `ConcatenatedLeaves` with `.HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer)`).
     - Add mappings (all column names are auto-converted to `snake_case` by the loop at lines 582–588; EVERY `byte[]` property MUST attach `byteArrayComparer`):
       ```csharp
       modelBuilder.Entity<TransferSessionRecipientEntity>(entity =>
       {
           entity.ToTable("ft_session_recipients");
           entity.HasKey(e => new { e.TransferId, e.RecipientIdentityId });
           entity.Property(e => e.RecipientIdentityId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.HasOne<TransferSessionEntity>()
               .WithMany()
               .HasForeignKey(e => e.TransferId)
               .OnDelete(DeleteBehavior.Cascade);
       });
       modelBuilder.Entity<TransferSessionSelectedFileEntity>(entity =>
       {
           entity.ToTable("ft_session_selected_files");
           entity.HasKey(e => new { e.TransferId, e.FileIndex });
           entity.HasOne<TransferSessionEntity>()
               .WithMany()
               .HasForeignKey(e => e.TransferId)
               .OnDelete(DeleteBehavior.Cascade);
       });
       // Independent table (no FK to ft_transfer_sessions, matching ft_session_certificates) so write order is unconstrained.
       modelBuilder.Entity<TransferSessionTransportContextEntity>(entity =>
       {
           entity.ToTable("ft_session_transport_contexts");
           entity.HasKey(e => e.TransferId);
           entity.Property(e => e.LocalIdentityId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.SwarmRendezvousId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.EphemeralPeerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.StreamAuthToken).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.RelayPipeToken).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.SenderCertSha256).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
       });
       modelBuilder.Entity<TransferSessionDirectCandidateEntity>(entity =>
       {
           entity.ToTable("ft_session_direct_candidates");
           entity.HasKey(e => new { e.TransferId, e.Host, e.Port });
           entity.HasOne<TransferSessionTransportContextEntity>()
               .WithMany()
               .HasForeignKey(e => e.TransferId)
               .OnDelete(DeleteBehavior.Cascade);
       });
       modelBuilder.Entity<FilterPolicyBlockedPeerEntity>(entity =>
       {
           entity.ToTable("ft_filter_policy_blocked_peers");
           entity.HasKey(e => new { e.OwnerId, e.BlockedPeerId });
           entity.Property(e => e.OwnerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.Property(e => e.BlockedPeerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.HasOne<FilterPolicyEntity>()
               .WithMany()
               .HasForeignKey(e => e.OwnerId)
               .OnDelete(DeleteBehavior.Cascade);
       });
       modelBuilder.Entity<FilterPolicyBlockedRelayEntity>(entity =>
       {
           entity.ToTable("ft_filter_policy_blocked_relays");
           entity.HasKey(e => new { e.OwnerId, e.RelayEndpointUri });
           entity.Property(e => e.OwnerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
           entity.HasOne<FilterPolicyEntity>()
               .WithMany()
               .HasForeignKey(e => e.OwnerId)
               .OnDelete(DeleteBehavior.Cascade);
       });
       ```
  5. **Update `Percolator.Infrastructure/Persistence/SqliteDatabaseMigrator.cs` (Zero EF Migrations, Zero Legacy File-Transfer DDL):**
     - Delete `ApplyFileTransferSchemaUpgradesAsync` (lines 71–120) and its call on line 51.
     - Delete lines 193–201 inside `ApplyPreKeySchemaUpgradesAsync` (the hardcoded PascalCase `CREATE TABLE IF NOT EXISTS "ft_session_certificates"` block, which conflicts with `PercolatorDbContext`'s `ToSnakeCase` convention).
     - Do NOT add any EF Core migrations and do NOT add manual `CREATE TABLE` / `ALTER TABLE` DDL: `MigrateAsync` lines 42–47 already execute `_dbContext.Database.GenerateCreateScript()` with `CREATE TABLE IF NOT EXISTS` for every entity in `PercolatorDbContext` using exact `snake_case` columns.
  6. **Create `Percolator.Infrastructure/Persistence/Repositories/SqliteTransferSessionTransportStore.cs`:**
     - Constructor: `public SqliteTransferSessionTransportStore(IDbContextFactory<PercolatorDbContext> dbContextFactory)` (thread-safe singleton, matching `SqliteTransferSessionCertificateStore` and `SqliteFilterPolicyRepository`).
     - `SaveTransportContextAsync`: open `await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct)`, upsert `TransferSessionTransportContextEntity` by `TransferId`, remove existing `TransferSessionDirectCandidates` rows for `TransferId`, insert deduplicated `(Host, Port)` rows from `context.DirectCandidates`, `await db.SaveChangesAsync(ct)`.
     - `GetTransportContextAsync`: open short-lived `PercolatorDbContext`, query `TransferSessionTransportContexts.AsNoTracking()` + `TransferSessionDirectCandidates.AsNoTracking()` for `sessionId.Value`; validate lengths (`SwarmRendezvousId == 32`, `EphemeralPeerId == 32`, `StreamAuthToken == 32`, `LocalIdentityId == 16`) and reconstruct `TransferSessionTransportContext`.
     - `DeleteTransportContextAsync`: open short-lived `PercolatorDbContext`, delete matching `TransferSessionTransportContextEntity` (cascade deletes `ft_session_direct_candidates`). Note: do NOT call `DeleteTransportContextAsync` from `CancelTransferAsync` / `HandleCancelAsync` because `SqlFileTransferQueryService` needs `ChannelId` so cancelled transfers stay visible in their chat thread; reserve `DeleteTransportContextAsync` for explicit transfer deletion/pruning.
  7. **Refactor `Percolator.Infrastructure/Persistence/Repositories/SqliteTransferSessionRepository.cs`:**
     - Delete `using System.Collections.Concurrent;`, `using System.Text.Json;`, `_transportContexts`, `SaveTransportContextAsync`, `GetTransportContextAsync`, `ApplyTransportContext`, `PersistedCandidateDto`, and the unused orphan `SaveBitfieldAsync` method.
     - In `SaveSessionAsync`:
       - Wrap writes in a single DB transaction (`await using var tx = await _dbContext.Database.BeginTransactionAsync(ct)`).
       - Set `existing.AllFilesSelected = session.SelectedFiles != null && session.SelectedFiles.SelectedCount == session.Manifest.TotalFiles.Count;`.
       - Replace `TransferSessionRecipients` rows for `transferGuid` (load tracked existing rows -> `RemoveRange` -> insert distinct recipient `byte[]` for `OneToOne` or `MultiPeer`, none for `IncomingStaged`).
       - Upsert `TransferBitfields` and `await _dbContext.SaveChangesAsync(ct)`.
       - **High-file-count bulk persistence (using the active `DbConnection` + `tx.GetDbTransaction()` — zero EF `ChangeTracker` bloat on 50k+ files):**
         - `ft_manifest_files` (insert-once when `!hasFiles`): execute a single reused parameterized `INSERT INTO ft_manifest_files (transfer_id, file_index, relative_path, file_size_bytes, start_offset, last_modified_unix_seconds) VALUES (@tid, @idx, @path, @size, @offset, @mtime)` command across `session.Manifest.Files` (formats `transferGuid` identically to EF Core's SQLite `Guid` text representation via `transferGuid.ToString().ToUpperInvariant()` — verify against EF Core's `GuidToStringConverter` output in `SqliteTransferSessionRepositoryTests`).
         - `ft_session_selected_files`: execute `DELETE FROM ft_session_selected_files WHERE transfer_id = @tid;`, then **ONLY if `session.SelectedFiles != null && !existing.AllFilesSelected`** (partial selection), bulk-insert the subset using a single reused parameterized `INSERT INTO ft_session_selected_files (transfer_id, file_index) VALUES (@tid, @idx)` command. Full-bundle transfers (`AllFilesSelected == true`) and `PendingConsent` (`SelectedFiles == null`) write **0 rows**!
       - Commit `await tx.CommitAsync(ct)`.
     - Implement `GetExpiredPendingOfferIdsAsync(DateTimeOffset createdBeforeUtc, CancellationToken ct)`: query ONLY `TransferId` where `State == (int)TransferSessionState.PendingConsent` (filtering `CreatedAtUtc < createdBeforeUtc` in memory since EF SQLite stores `DateTimeOffset` as ISO-8601 text), returning `IReadOnlyList<TransferSessionId>` without touching `ft_manifest_files`.
     - In `GetActiveSessionsAsync`: batch-load `ManifestFiles`, `TransferBitfields`, `TransferSessionRecipients`, and `TransferSessionSelectedFiles` (only for sessions with `!e.AllFilesSelected`) in 4 flat `AsNoTracking()` `.Where(x => transferIds.Contains(x.TransferId))` queries and reconstitute via a pure in-memory `ReconstituteSession(entity, files, bitfield, recipients, selectedFileIndices)` helper (shared with `GetSessionByIdAsync`), where `entity.AllFilesSelected == true` reconstitutes `SelectedFileIndices.All(manifest.TotalFiles)`.
  8. **Refactor `Percolator.Infrastructure/Persistence/Repositories/SqliteFilterPolicyRepository.cs`:**
     - Delete `using System.Text.Json;`.
     - In `SavePolicyAsync`: upsert `FilterPolicyEntity` (`OwnerId`, `UpdatedAtUtc`), load existing `FilterPolicyBlockedPeers` and `FilterPolicyBlockedRelays` for `ownerBytes`, `RemoveRange` existing child rows, add current `policy.BlockedPeers` (`BlockedPeerId = p.ToByteArray()`) and `policy.BlockedRelays` (`RelayEndpointUri = r.Value`), `SaveChangesAsync(ct)`.
     - In `GetPolicyAsync`: load `FilterPolicyEntity`, `FilterPolicyBlockedPeers`, and `FilterPolicyBlockedRelays` with `AsNoTracking()`, reconstruct `FileTransferFilterPolicy` using `PublicIdentityId.FromBytes(r.BlockedPeerId)` and `RelayPipeEndpointUri.Parse(r.RelayEndpointUri)`.
  9. **Keep `SqlFileTransferQueryService.cs` compiling in `FT-007`** (full singleton + batch refactor follows in `FT-008`):
     - Replace removed `SelectedFilesJson`, `RecipientsJson`, `LocalRootPath`, `ChannelId` reads on `TransferSessionEntity` with `AllFilesSelected` + queries to `TransferSessionSelectedFiles`, `TransferSessionRecipients`, and `TransferSessionTransportContexts`.
  10. **Update Call Sites & DI:**
      - `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (`AddFileTransferInfrastructure()`): add `services.AddSingleton<ITransferSessionTransportStore, SqliteTransferSessionTransportStore>();`. (Automatically wires Desktop via `DesktopStage2SessionFactory`, Simulator via `AddAuthenticatedInfrastructure`, Integration tests via `TestPersonaNode`, and `DependencyInjectionTests`.)
      - `Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs`: inject `ITransferSessionTransportStore transportStore` (append to constructor parameter list, non-null guard); migrate all 7 calls (lines 183, 342, 359, 456, 481, 603, 625) from `_sessionRepository.*TransportContextAsync` to `_transportStore.*TransportContextAsync`.
      - `Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferPayloadHandler.cs`: inject `ITransferSessionTransportStore transportStore` (append to constructor parameter list, non-null guard); migrate all 4 calls (lines 122, 213, 256, 360) from `_sessionRepository.*TransportContextAsync` to `_transportStore.*TransportContextAsync`.
  11. **Tests to Update / Add:**
      - `Percolator.Apps.FileTransfer.Tests/TestDoubles/TestDoubles.cs`: remove transport context fields/methods from `InMemoryTransferSessionRepository`, implement `GetExpiredPendingOfferIdsAsync`, and add `InMemoryTransferSessionTransportStore : ITransferSessionTransportStore`.
      - Update constructor call sites in:
        - `Percolator.Apps.FileTransfer.Tests/Features/IngressControl/Tests/IngressControlTests.cs` (lines 104, 130, and any other `new FileTransferPayloadHandler`)
        - `Percolator.Apps.FileTransfer.Tests/Features/TransferNegotiation/Tests/TransferNegotiationTests.cs` (lines 55, 331, 373)
        - `Percolator.Apps.FileTransfer.Tests/Ports/Tests/ActionHandlerAndQueryTests.cs` (lines 59, 237, 311, 330)
        - `Percolator.Apps.FileTransfer.Tests/Features/Runtime/TransferSessionRuntimeTests.cs` (lines 455 register `ITransferSessionTransportStore`, lines 502, 515)
      - `Percolator.Infrastructure.Tests/Plugins/FileTransfer/SqliteTransferSessionRepositoryTests.cs`: instantiate `SqliteTransferSessionTransportStore(_db.ContextFactory)`, test `SaveTransportContextAsync`/`GetTransportContextAsync`/`DeleteTransportContextAsync` (including `DirectCandidates` round-trip and overwrite), test `SaveSessionAsync` with full selection (`ft_session_selected_files` row count == 0 and `AllFilesSelected == true`) vs partial selection + `AuthorizeGroupPeer` without PK collisions, test bulk manifest insert + `GetSessionByIdAsync` round-trip with 1,000+ files, test `GetExpiredPendingOfferIdsAsync`, and test `FilterPolicyRepository` blocking then unblocking peers and relays.
      - `Percolator.Infrastructure.Tests/Composition/DependencyInjectionTests.cs`: assert `sp.GetRequiredService<ITransferSessionTransportStore>().Should().NotBeNull();`.
- **Acceptance Criteria:**
  - Zero EF Core migrations added; zero manual file-transfer DDL left in `SqliteDatabaseMigrator.cs`.
  - `PRAGMA busy_timeout = 5000;` enabled in `SqlCipherPragmaInterceptor`.
  - Full-bundle sessions store `AllFilesSelected = true` and **0 rows** in `ft_session_selected_files`; `ft_manifest_files` uses bulk parameterized SQL insert inside a transaction.
  - Zero `System.Text.Json` imports or calls across `SqliteTransferSessionRepository.cs`, `SqliteTransferSessionTransportStore.cs`, `SqliteFilterPolicyRepository.cs`, `SqlFileTransferQueryService.cs`, and `FileTransferEntities.cs`.
  - Zero `ConcurrentDictionary` or Default Interface Methods on `ITransferSessionRepository` or `SqliteTransferSessionRepository`.
  - Zero N+1 queries in `SqliteTransferSessionRepository.GetActiveSessionsAsync`.
  - All test suites build and pass 100%.

---

#### Ticket `FT-008` — CQRS Read Purity, Singleton `IDbContextFactory` Lifetime, N+1 Elimination & Paginated Read-Model Fixes
- **Layer / Scope:** `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`
- **Dependencies:** `FT-007`
- **Architectural Defects Addressed:**
  - `FileTransferQueryService` in `Percolator.Apps.FileTransfer` violates CQRS Rule 8 by injecting `ITransferSessionRepository` and rehydrating domain aggregates, and causes dual DI registration with `SqlFileTransferQueryService`.
  - `SqlFileTransferQueryService` violates Anti-Shortcut Rule 16 (captured scoped `PercolatorDbContext`): it injects scoped `PercolatorDbContext` and is registered `AddScoped`, yet `ChatViewModel` (cached across scopes in `SessionScopeFactory` LRU cache) holds `IFileTransferQueryService` and polls it on a timer. Every other Desktop UI query service (`SqlChatMessageQueryService`, `SqlConversationListQueryService`, `SqlPeerContactQueryService`, `SqlPersonaRelayQueryService`) uses `IDbContextFactory<PercolatorDbContext>` and is registered `AddSingleton`.
  - `SqlFileTransferQueryService.GetAllTransfersAsync` executes an N+1 query loop per session.
  - `SqlFileTransferQueryService.cs:163` calls `SeedRatio.FromMultiplier(entity.TargetSeedRatio)` unconditionally, which throws `ArgumentOutOfRangeException` on `SeedRatio.Unlimited` (stored as `-1.0`).
  - `SqlFileTransferQueryService.GetManifestFilesAsync` (lines 89–101) queries `TransferBitfields` and discards it, faking per-file `IsCompleted` via session-level completion instead of checking the file's chunk range bits, and offers no pagination for large multi-thousand-file bundles.
  - `SqlFileTransferQueryService.cs:127` computes `downloadedBytes = verifiedPieces * chunkSizeBytes`, which overcounts when the final partial chunk is verified before earlier chunks.
- **Exact Implementation Specification:**
  1. **Delete Aggregate-Rehydrating Query Service & Add Optional Pagination to Port:**
     - Delete `Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferQueryService.cs`.
     - In `Percolator.Apps.FileTransfer/DependencyInjection/ServiceCollectionExtensions.cs`: remove line 25 (`collection.AddScoped<IFileTransferQueryService, FileTransferQueryService>();`).
     - In `Percolator.Apps.FileTransfer/Ports/IFileTransferQueryService.cs` (line 9): update signature to:
       `ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(TransferSessionId sessionId, int skip = 0, int? take = null, CancellationToken ct = default);`
  2. **Refactor `Percolator.Infrastructure/Persistence/Repositories/SqlFileTransferQueryService.cs`:**
     - Change constructor to `public SqlFileTransferQueryService(IDbContextFactory<PercolatorDbContext> dbContextFactory)` and register `services.AddSingleton<IFileTransferQueryService, SqlFileTransferQueryService>();` in `InfrastructureServiceCollectionExtensions.AddFileTransferInfrastructure()` (replacing `AddScoped`).
     - In `GetAllTransfersAsync(CancellationToken ct)`:
       - Open `await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);`.
       - Execute 4 flat `AsNoTracking()` batch queries (return `Array.Empty<TransferSessionReadModel>()` early if `entities.Count == 0`):
         1. `List<TransferSessionEntity> entities = await db.TransferSessions.AsNoTracking().ToListAsync(ct);`
         2. `Dictionary<Guid, TransferBitfieldEntity> bitfieldMap = await db.TransferBitfields.AsNoTracking().Where(b => transferIds.Contains(b.TransferId)).ToDictionaryAsync(b => b.TransferId, ct);`
         3. `ILookup<Guid, byte[]> recipientsLookup = (await db.TransferSessionRecipients.AsNoTracking().Where(r => transferIds.Contains(r.TransferId)).ToListAsync(ct)).ToLookup(r => r.TransferId, r => r.RecipientIdentityId);`
         4. `Dictionary<Guid, TransferSessionTransportContextEntity> transportMap = await db.TransferSessionTransportContexts.AsNoTracking().Where(t => transferIds.Contains(t.TransferId)).ToDictionaryAsync(t => t.TransferId, ct);`
       - Project synchronously in memory via `ProjectReadModel(entity, bitfield, recipients, transportCtx)`.
     - In `GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct)`:
       - Open short-lived `PercolatorDbContext`, query the 4 tables for `sessionId.Value` with `AsNoTracking()`, and delegate to the same synchronous `ProjectReadModel(...)`.
     - In `ProjectReadModel(...)`:
       - Compute `totalPieces`: `bitfield?.TotalPieces ?? (entity.TotalSizeBytes == 0 ? 1 : (int)((entity.TotalSizeBytes + entity.ChunkSizeBytes - 1) / entity.ChunkSizeBytes))`.
       - Compute exact `downloadedBytes` and `verifiedPieces`: if `bitfield is { BitfieldBytes.Length: > 0 }`, parse `Bitfield parsed = Bitfield.FromBytes(bitfield.BitfieldBytes, ChunkCount.FromInt(totalPieces))`; if `parsed.IsComplete`, `downloadedBytes = entity.TotalSizeBytes`, else sum `Math.Min((long)entity.ChunkSizeBytes, Math.Max(0, entity.TotalSizeBytes - (long)i * entity.ChunkSizeBytes))` for each verified chunk `i` (fast path: when `i < totalPieces - 1`, chunk size is always `entity.ChunkSizeBytes`, so `downloadedBytes = (long)(parsed.PopCount - (hasLastChunk ? 1 : 0)) * entity.ChunkSizeBytes + (hasLastChunk ? lastChunkBytes : 0)` in **O(1)** instead of looping up to 1,000,000 chunks!).
       - Fix `targetSeedRatio`: `entity.TargetSeedRatio < 0 ? SeedRatio.Unlimited : SeedRatio.FromMultiplier(entity.TargetSeedRatio)`.
       - Reconstruct `TransferTopology` from `recipients` (`PublicIdentityId.FromBytes`): `1` -> `OneToOne` (guard empty with fallback `IncomingStaged`), `2` -> `MultiPeer`, `_` -> `IncomingStaged`.
       - Project `localRoot` from `transportCtx?.LocalRootPath` and `channelId` from `transportCtx is { ChannelId: var cid } && cid != Guid.Empty ? ChannelId.FromGuid(cid) : null`.
     - In `GetManifestFilesAsync(TransferSessionId sessionId, int skip = 0, int? take = null, CancellationToken ct = default)`:
       - Open short-lived `PercolatorDbContext`; load `TransferSessionEntity` and `TransferBitfieldEntity` with `AsNoTracking()`.
       - Query `ManifestFiles` with `.Where(f => f.TransferId == sessionId.Value).OrderBy(f => f.FileIndex)`, applying `.Skip(skip)` when `skip > 0` and `.Take(take.Value)` when `take.HasValue`.
       - Only query `TransferSessionSelectedFiles` when `!sessionEntity.AllFilesSelected`:
         - If `sessionEntity.AllFilesSelected` is `true`, every file has `isSelected = true` (zero `ft_session_selected_files` query).
         - Else load `HashSet<int> selectedIndices` for `sessionId.Value` (if `selectedIndices.Count == 0` AND `sessionEntity.State == (int)TransferSessionState.PendingConsent`, treat all as unselected/selectable preview; otherwise `isSelected = selectedIndices.Contains(file.FileIndex)`).
       - Parse `Bitfield? localBitfield = bitfieldEntity is { BitfieldBytes.Length: > 0 } ? Bitfield.FromBytes(bitfieldEntity.BitfieldBytes, ChunkCount.FromInt(bitfieldEntity.TotalPieces)) : null;`.
       - Compute per-file `isCompleted`:
         - If `file.FileSizeBytes == 0` -> `true`.
         - Else if `!isSelected || localBitfield == null` -> `false`.
         - Else compute `firstChunk = (int)(file.StartOffset / sessionEntity.ChunkSizeBytes)` and `lastChunk = (int)((file.StartOffset + file.FileSizeBytes - 1) / sessionEntity.ChunkSizeBytes)`, returning `true` iff every chunk `c` in `[firstChunk..lastChunk]` satisfies `localBitfield.HasChunk(ChunkIndex.FromInt(c))`.
  3. **Update / Add Unit Tests:**
     - `Percolator.Apps.FileTransfer.Tests/Ports/Tests/ActionHandlerAndQueryTests.cs`: delete `_queryService` field (line 33), initialization (line 72), and `UT_QRY_01_TransferSessionQueryProjectionAccuracy` (lines 413–445).
     - Create `Percolator.Infrastructure.Tests/Persistence/Repositories/SqlFileTransferQueryServiceTests.cs` against `SqliteTestDb`:
       - `GetAllTransfersAsync`: verifies `OneToOne`, `MultiPeer`, `IncomingStaged`, `SeedRatio.Unlimited` (`-1.0`, no throw), `ChannelId`, and `LocalRootPath` projection across multiple sessions.
       - `GetTransferByIdAsync`: verifies exact O(1) `DownloadedBytes` when only the final partial chunk is verified (e.g., 98,304-byte bundle with 65,536-byte chunks where only chunk 1 is set -> `DownloadedBytes == 32,768`, NOT `65,536`), `UploadedBytes`, and `CurrentSeedRatio`.
       - `GetManifestFilesAsync`: verifies partial completion (file 0 chunk complete -> `IsCompleted == true`; file 1 chunk missing -> `IsCompleted == false`; unselected file -> `IsSelected == false, IsCompleted == false`; 0-byte file -> `IsCompleted == true`), `AllFilesSelected = true` with 0 child rows, and `skip`/`take` pagination.
       - Verifies concurrent `Task.WhenAll(svc.GetAllTransfersAsync().AsTask(), svc.GetTransferByIdAsync(id).AsTask())` succeeds safely on the singleton instance.
- **Acceptance Criteria:**
  - `FileTransferQueryService.cs` is deleted and `IFileTransferQueryService` is registered solely in `AddFileTransferInfrastructure()` as `Singleton`.
  - Zero queries inside loops in `SqlFileTransferQueryService`; `DownloadedBytes` is O(1); `GetManifestFilesAsync` supports `skip`/`take` pagination and skips `ft_session_selected_files` when `AllFilesSelected == true`.
  - `SeedRatio.Unlimited`, partial-chunk byte counts, pagination, and per-file bitfield completion all verified by `SqlFileTransferQueryServiceTests`.

---

#### Ticket `FT-009` — Application Event Bus ICE Trickling, Control Framing Deduplication, Synthetic `ChannelId` Removal & DI Hygiene
- **Layer / Scope:** `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Desktop`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.ApplicationIntegrationTests`
- **Dependencies:** `FT-008`
- **Architectural Defects Addressed:**
  - `ITransferPeerConnection` exposes a raw C# `event Action<IceSignalOutboundEvent>? SignalProduced;`, forcing `TransferSessionRuntime.WireIceCandidateForwarder` to spawn untracked fire-and-forget `_ = Task.Run(...)` tasks on every ICE candidate and forcing `FileTransferPayloadHandler` to synthesize `Func<FileTransferIceSignalPayload, CancellationToken, ValueTask>` closures (`CreateIceSignalDispatcher`, lines 405–446) plus a SQLite lookup on every inbound ICE candidate. Trickle ICE candidates are low-frequency point-in-time lifecycle facts that fit the existing Application Event Bus (`IEventPublisher` / `IEventSubscriber`, `INV-APP-EVENT-BUS-DECOUPLING`), NOT custom channels on application ports.
  - `WebRtcPeerConnectionAdapter.OnLocalDescription` (lines 389–399) writes dead `SdpOffer`/`SdpAnswer` events into `_signalQueue` even though `CreateOfferAsync`/`AcceptOfferAndCreateAnswerAsync` already return SDP directly via `_localDescriptionTcs` and every consumer ignores non-`IceCandidate` events.
  - Control opcode framing + `RelayedGroup`/`RelayedOneToOne` `OutboundPayloadContext` construction is copy-pasted 6 times across `FileTransferActionHandler` (4x) and `FileTransferPayloadHandler` (2x).
  - `FileTransferActionHandler.CancelTransferAsync` (line 627) fabricates a synthetic `ChannelId.FromGuid(sessionId.Value)` when `transportContext` is missing, violating Anti-Shortcut Rule 3 (no synthetic entities/IDs).
  - Dead/duplicate DI wiring: `NullTransferStreamPool.cs` has zero production callers; `ServiceCollectionExtensions.cs` (lines 26–30) registers 5 per-session stateful coordinators never resolved from DI; `InfrastructureServiceCollectionExtensions.cs:408` duplicates `ITransferOfferReaperService`; `DesktopNetworkEndpointDiscoveryPort` manually `new`s `NetworkInterfaceEndpointDiscoveryPort` per call; `DesktopSessionServiceCollectionExtensions.cs` (lines 97–113) duplicates `AddFileTransferApp()`.
- **Exact Implementation Specification:**
  1. **Publish Trickle ICE Candidates via Application Event Bus (`IEventPublisher` / `IEventSubscriber`):**
     - Create `Percolator.Apps.FileTransfer/Ports/TransferIceCandidateDiscoveredEvent.cs`:
       ```csharp
       using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
       using Percolator.PluginSdk.Events;
       using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

       namespace Percolator.Apps.FileTransfer.Ports;

       public sealed record TransferIceCandidateDiscoveredEvent(
           Guid EventId,
           DateTimeOffset OccurredOnUtc,
           TransferSessionId SessionId,
           PublicIdentityId RemotePeerId,
           string Candidate,
           string? CandidateMid,
           CertSha256Fingerprint? LocalDtlsFingerprint) : IApplicationEvent;
       ```
     - In `Percolator.Apps.FileTransfer/Ports/ITransferPeerTransport.cs`:
       - Delete `event Action<IceSignalOutboundEvent>? SignalProduced;` from `ITransferPeerConnection` and delete `public sealed record IceSignalOutboundEvent(...)`. Zero custom events or `ChannelReader` members remain on `ITransferPeerConnection`.
     - In `Percolator.Infrastructure/Plugins/FileTransfer/WebRtc/WebRtcPeerConnectionAdapter.cs`:
       - Add constructor parameters `IEventPublisher eventPublisher, IDateTimeProvider clock` (non-null guards):
         `public WebRtcPeerConnectionAdapter(DirectPeerConnectionOptions options, IEventPublisher eventPublisher, IDateTimeProvider clock, Action onDisposed)`.
       - Delete `public event Action<IceSignalOutboundEvent>? SignalProduced;`.
       - Keep private `_candidateQueue` (`Channel<TransferIceCandidateDiscoveredEvent>`) strictly as an internal libdatachannel native-callback offload buffer (so C++ callback threads never execute async managed code or contend on managed locks), paired with `private readonly CancellationTokenSource _disposeCts = new();`.
       - In `OnLocalDescription` (lines 389–399): delete the dead `SdpOffer`/`SdpAnswer` queue write; keep `_localDescriptionTcs.TrySetResult(sdp)`.
       - In `OnLocalCandidate` (lines 409–415): write `new TransferIceCandidateDiscoveredEvent(Guid.NewGuid(), _clock.UtcNow, SessionId, RemotePeerId, candidate.Content, candidate.Mid, LocalDtlsFingerprint)` to `_candidateQueue`.
       - Replace `PumpOutboundSignalsAsync()` with `PumpOutboundCandidatesAsync()`: read `await foreach (TransferIceCandidateDiscoveredEvent evt in _candidateQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))` and dispatch via `await _eventPublisher.PublishAsync(evt, _disposeCts.Token).ConfigureAwait(false)` (catch `OperationCanceledException` to break; catch `Exception` so a single dispatch fault never terminates candidate trickling).
       - In `DisposeAsync()`: call `_disposeCts.Cancel();`, `_candidateQueue.Writer.TryComplete();`, `await _candidatePumpTask.ConfigureAwait(false);` (catching `OperationCanceledException`), `_disposeCts.Dispose();`, then run off-thread native `_peerConnection.Dispose()`.
     - In `Percolator.Infrastructure/Plugins/FileTransfer/TransferPipeTlsStreamTransport.cs` (`TransferPeerTransport`, lines 625–652):
       - Update constructor to `public TransferPeerTransport(PeerGrpcChannelFactory channelFactory, ITransferSessionCertificateStore certificateStore, IEventPublisher eventPublisher, IDateTimeProvider clock)` (all 4 are Singletons registered in every host DI container; `InfrastructureServiceCollectionExtensions.cs:402` auto-resolves without change).
       - Pass `_eventPublisher, _clock` into `new WebRtcPeerConnectionAdapter(options, _eventPublisher, _clock, () => UnregisterConnection(options.SessionId, adapter!))`.
  2. **Extract Shared `FileTransferControlFraming` & Eliminate `CreateIceSignalDispatcher` Closures:**
     - Create `Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferControlFraming.cs`:
       ```csharp
       using Percolator.Domain.Channels.ValueObjects;
       using Percolator.Domain.Identities.ValueObjects;
       using Percolator.PluginSdk;
       using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

       namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

       internal static class FileTransferControlFraming
       {
           public static byte[] FramePayload<TPayload>(
               IPayloadSerializer serializer,
               FileTransferControlOpcode opcode,
               TPayload payload)
           {
               ReadOnlyMemory<byte> serialized = serializer.Serialize(payload);
               byte[] framed = new byte[1 + serialized.Length];
               framed[0] = (byte)opcode;
               serialized.Span.CopyTo(framed.AsSpan(1));
               return framed;
           }

           public static OutboundPayloadContext BuildOutboundContext(
               ChannelId channelId,
               PublicIdentityId localIdentityId,
               PublicIdentityId? directRecipientId,
               bool isGroupTransfer,
               byte[] framedPayload)
           {
               return isGroupTransfer
                   ? new OutboundPayloadContext(
                       channelId,
                       localIdentityId,
                       RecipientIdentityId: null,
                       DeviceId.Primary,
                       AppId.FileTransferControl,
                       framedPayload,
                       DeliveryRoute.RelayedGroup)
                   : new OutboundPayloadContext(
                       channelId,
                       localIdentityId,
                       directRecipientId ?? throw new ArgumentNullException(nameof(directRecipientId)),
                       DeviceId.Primary,
                       AppId.FileTransferControl,
                       framedPayload,
                       DeliveryRoute.RelayedOneToOne);
           }
       }
       ```
     - In `Percolator.Apps.FileTransfer/Ports/ITransferSessionRuntime.cs`:
       - Remove the `Func<FileTransferIceSignalPayload, CancellationToken, ValueTask>` parameter from BOTH `HandleInboundIceSignalAsync` (line 56) and `InitiateDirectConnectionAsync` (line 62).
     - In `Percolator.Apps.FileTransfer/Features/Runtime/TransferSessionRuntime.cs`:
       - Inject `IPayloadSerializer serializer` and `IBackgroundPayloadDispatcher payloadDispatcher` (non-null guards; both are already Singletons in DI).
       - Subscribe in constructor: `_iceCandidateSubscription = eventSubscriber.Subscribe<TransferIceCandidateDiscoveredEvent>(OnLocalIceCandidateDiscoveredAsync);`, and call `_iceCandidateSubscription.Dispose();` in `DisposeAsync()`.
       - Delete `WireIceCandidateForwarder` (lines 795–825) and its call sites (lines 216, 390).
       - Add `OnLocalIceCandidateDiscoveredAsync(TransferIceCandidateDiscoveredEvent ev, CancellationToken ct)`:
         - Return immediately if `Volatile.Read(ref _disposed) != 0`, or `!_sessions.TryGetValue(ev.SessionId, out ActiveTransferHandle? handle)`, or `handle.SessionCts.IsCancellationRequested`, or `!handle.PeerConnections.ContainsKey(ev.RemotePeerId)`, or `!handle.PinnedPeerCerts.ContainsKey(ev.RemotePeerId)`.
         - Build `new FileTransferIceSignalPayload(ev.SessionId, IceSignalKind.IceCandidate, ev.Candidate, ev.CandidateMid, ev.LocalDtlsFingerprint)`, link `(ct, handle.SessionCts.Token)`, and `await DispatchIceSignalAsync(handle, ev.RemotePeerId, payload, linkedCts.Token)` (catching `OperationCanceledException` / `ObjectDisposedException`).
       - Add `DispatchIceSignalAsync(ActiveTransferHandle handle, PublicIdentityId remotePeerId, FileTransferIceSignalPayload signalPayload, CancellationToken ct)`:
         - Apply `TargetPeerId = remotePeerId` when `handle.TransportContext.IsGroupTransfer` is true; frame via `FileTransferControlFraming.FramePayload(_serializer, FileTransferControlOpcode.IceSignal, wirePayload)` and `FileTransferControlFraming.BuildOutboundContext(ctx.ChannelId, ctx.LocalIdentityId, ctx.IsGroupTransfer ? null : remotePeerId, ctx.IsGroupTransfer, framed)`; return `_payloadDispatcher.DispatchAsync(outbound, ct)`.
       - Replace `sendSignalReplyAsync` / `sendSignalAsync` calls on lines 250 (`SdpAnswer`), 400 (`SdpOffer`), and 419 (`PipeRendezvous`) with `DomainResult sendResult = await DispatchIceSignalAsync(...)`. If `sendResult.IsFailure`: for `SdpAnswer`/`SdpOffer`, remove & dispose the newly created peer connection and return `sendResult`; for `PipeRendezvous`, return `sendResult` without spawning `pipeConnectTask`.
     - In `Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferPayloadHandler.cs`:
       - Delete `CreateIceSignalDispatcher(...)` (lines 405–446).
       - Update `HandleAcceptAsync` (line 225) and `HandlePeerJoinAsync` (line 323) to call `_runtime.InitiateDirectConnectionAsync(sessionId, senderId, ct)`.
       - Update `HandlePeerJoinAsync` (lines 303–317) to use `FileTransferControlFraming`.
       - Simplify `HandleIceSignalAsync` (lines 340–377): remove the unused `channelId` parameter and the redundant `_transportStore.GetTransportContextAsync(...)` DB read on every inbound ICE candidate, calling `return _runtime.HandleInboundIceSignalAsync(signal.TransferSessionId, senderId, signal, ct);` directly.
  3. **Eliminate Synthetic `ChannelId` Fallback:**
     - In `Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs` (`CancelTransferAsync`, lines 625–668):
       - Delete `ChannelId channelId = transportContext?.ChannelId ?? ChannelId.FromGuid(sessionId.Value);`.
       - Always save cancelled session state, stop runtime (`deleteCertificate: true`), and unregister tracker (when `!wasActiveInRuntime && transportContext != null`).
       - Guard network cancel dispatch strictly with `if (transportContext != null && transportContext.ChannelId.IsValid)`: construct and send `FileTransferCancelPayload` via `FileTransferControlFraming` using `transportContext.ChannelId` and `transportContext.LocalIdentityId` only when a real persisted `ChannelId` exists.
       - Add unit test in `ActionHandlerAndQueryTests.cs` verifying `CancelTransferAsync` without a transport context transitions session to `Cancelled` and sends zero wire payloads (no synthetic `ChannelId`).
  4. **Delete Dead Code & Deduplicate FileTransfer DI:**
     - Delete dead `Percolator.Apps.FileTransfer/Ports/NullTransferStreamPool.cs` (zero production callers).
     - In `Percolator.Infrastructure/Plugins/FileTransfer/NetworkInterfaceEndpointDiscoveryPort.cs`:
       - Replace `private readonly PortNumber? _listenPort;` with `private readonly Func<int?> _listenPortProvider;`.
       - Keep existing `public NetworkInterfaceEndpointDiscoveryPort(int? listenPort = null, bool allowLoopback = false)` (validates `listenPort` eagerly if non-null so `NetworkInterfaceEndpointDiscoveryPortTests` stay 100% compatible, then sets `_listenPortProvider = () => listenPort`).
       - Add `public NetworkInterfaceEndpointDiscoveryPort(Func<int?> listenPortProvider, bool allowLoopback = false)` (null guard on `listenPortProvider`).
       - In `DiscoverLocalCandidatesAsync`: read `int? currentPort = _listenPortProvider();`; if `!currentPort.HasValue || currentPort.Value is < 1 or > 65535` return empty; otherwise use `PortNumber.FromInt(currentPort.Value)`.
     - Delete `Percolator.Desktop/Common/Services/DesktopNetworkEndpointDiscoveryPort.cs`.
     - In `Percolator.Apps.FileTransfer/DependencyInjection/ServiceCollectionExtensions.cs`:
       - Change signature to `public static IServiceCollection AddFileTransferApp(this IServiceCollection collection, bool enableIngressByDefault = false)`.
       - Register `FileTransferIngressFilter` as:
         ```csharp
         collection.AddSingleton<FileTransferIngressFilter>(sp =>
             new FileTransferIngressFilter(sp.GetRequiredService<IFileTransferFilterPolicyRepository>())
             {
                 IsEnabled = enableIngressByDefault
             });
         ```
       - Delete the 5 unused per-session `AddScoped` registrations (lines 26–30: `TransferSessionCoordinator`, `DiskChunkAssembler`, `ChunkPipelineScheduler`, `SwarmCoordinator`, `EndgameCoordinator` — always constructed per-session inside `TransferSessionRuntime.CreateHandle` and never resolved from DI).
     - In `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`:
       - Delete duplicate line 408 (`services.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();` — already registered in `AddFileTransferApp()`).
     - In `Percolator.Desktop/Common/Session/DesktopSessionServiceCollectionExtensions.cs`:
       - Line 57: replace `DesktopNetworkEndpointDiscoveryPort` registration with:
         ```csharp
         services.AddSingleton<Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort>(sp =>
         {
             var currentPersona = sp.GetRequiredService<ICurrentPersonaService>();
             return new Percolator.Infrastructure.Plugins.FileTransfer.NetworkInterfaceEndpointDiscoveryPort(
                 () => currentPersona.CurrentPersona?.BoundPort,
                 allowLoopback: true);
         });
         ```
       - Lines 97–113: replace the 17 manually duplicated FileTransfer registrations (and fix the stale comment on lines 93–95) with `services.AddFileTransferApp(enableIngressByDefault: true);`.
  5. **Exact Test & Call-Site Updates:**
     - `Percolator.Apps.FileTransfer.Tests/TestDoubles/TestDoubles.cs`: delete `SignalProduced` and `EmitIceCandidate` from `FakeTransferPeerConnection`.
     - Pass `IPayloadSerializer` + `IBackgroundPayloadDispatcher` (`new DirectBackgroundPayloadDispatcher(payloadSender)`) to every `new TransferSessionRuntime(...)` call site:
       - `Percolator.Apps.FileTransfer.Tests/Features/IngressControl/Tests/IngressControlTests.cs` (line 35)
       - `Percolator.Apps.FileTransfer.Tests/Features/TransferNegotiation/Tests/TransferNegotiationTests.cs` (line 44)
       - `Percolator.Apps.FileTransfer.Tests/Ports/Tests/ActionHandlerAndQueryTests.cs` (lines 47, 309)
       - `Percolator.Apps.FileTransfer.Tests/Features/Runtime/TransferSessionRuntimeTests.cs` (line 492; also remove line 440's stale `NullTransferStreamPool` check while keeping line 439 asserting `ITransferStreamPool` is unregistered, and update line 167 `HandleInboundIceSignalAsync` to drop the lambda parameter).
     - Add unit test in `TransferSessionRuntimeTests.cs` verifying publishing `TransferIceCandidateDiscoveredEvent` on `node.EventBus` dispatches a framed `IceSignal` via `IBackgroundPayloadDispatcher` (with `TargetPeerId` + `RelayedGroup` for group sessions, `RelayedOneToOne` for 1:1 sessions, and silently ignored after session stop or peer eviction).
     - `Percolator.Infrastructure.Tests/Plugins/FileTransfer/WebRtcPeerTransportTests.cs`:
       - Create `InMemoryEventBus _eventBusA = new(NullLogger<InMemoryEventBus>.Instance);` and `_eventBusB` (disposed in `TearDownAsync`), pass `(_channelFactory, certStoreA, _eventBusA, clock)` / `(_channelFactory, certStoreB, _eventBusB, clock)` to `new TransferPeerTransport(...)` (lines 46–47).
       - Replace `SignalProduced +=` lambdas (lines 102–122 and 253–266) with `using IDisposable subA = _eventBusA.Subscribe<TransferIceCandidateDiscoveredEvent>(async (evt, ct) => { if (evt.SessionId == sessionId) { try { await connB.AddRemoteIceCandidateAsync(evt.Candidate, evt.CandidateMid ?? string.Empty, ct); } catch { } } });` (and symmetric `subB` on `_eventBusB`), eliminating all `_ = Task.Run` fire-and-forget lambdas in the test.
     - `Percolator.ApplicationIntegrationTests/Scenarios/TransferPipeTls13FallbackIntegrationTests.cs` (lines 55–56): pass `new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance)` (disposed in `TearDownAsync`) and `clock` to `new TransferPeerTransport(...)`.
     - `Percolator.ApplicationIntegrationTests/Scenarios/FileTransferEndToEndIntegrationTests.cs` (lines 361 and 517): drop the `(_, _) => ValueTask.CompletedTask` lambda argument from `HandleInboundIceSignalAsync` and `InitiateDirectConnectionAsync`.
- **Acceptance Criteria:**
  - Zero `event Action<...>` delegates or `ChannelReader` members on `ITransferPeerConnection`; zero `_ = Task.Run` fire-and-forget tasks in `TransferSessionRuntime` or `WebRtcPeerTransportTests`.
  - Zero `CreateIceSignalDispatcher` closures or `Func<FileTransferIceSignalPayload, ...>` callback parameters on `ITransferSessionRuntime`.
  - Zero `R3` package references added to `Percolator.Apps.FileTransfer` or `Percolator.Infrastructure`.
  - Zero `ChannelId.FromGuid(sessionId.Value)` in `FileTransferActionHandler`.
  - `NullTransferStreamPool.cs` and `DesktopNetworkEndpointDiscoveryPort.cs` deleted; `DesktopSessionServiceCollectionExtensions.cs` calls `services.AddFileTransferApp(enableIngressByDefault: true)`.
  - All test suites pass 100%.

---

#### Ticket `FT-010` — Ingress Policy Owner-Scoping Fix, Outbound Failure Propagation, 2-Member Group Support & Serialized Runtime Checkpointing
- **Layer / Scope:** `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.ApplicationIntegrationTests`
- **Dependencies:** `FT-009`
- **Architectural Defects Addressed:**
  - **Security Bug (Blocked Peers/Relays Never Enforced in Production):** Both `FileTransferIngressFilter` (`EvaluateAsync` line 32, `Evaluate` line 46) and `FileTransferPayloadHandler` (`HandleInboundAsync` line 56) call `_policyRepository.GetPolicyAsync(context.SenderIdentityId, ...)`. `FilterPolicyEntity.OwnerId` is the **local recipient persona's** ID; querying by the remote sender's ID always returns `DefaultPermissive` in SQLite. This was masked in unit tests because `InMemoryFilterPolicyRepository` ignored its `ownerId` parameter and returned a single `Policy` field.
  - **Swallowed Outbound Send Failures:** `FileTransferActionHandler` ignores the `DomainResult` returned by every `_payloadSender.SendPayloadAsync(...)` call (`InitiateTransferAsync` lines 250/263, `AcceptTransferAsync` lines 412/435, `ResumeTransferAsync` lines 547/571, `CancelTransferAsync` lines 652/666), returning `Success` and leaving orphaned active runtime handles when outbox/ratchet dispatch fails.
  - **2-Member Group Chat Misclassification:** `FileTransferActionHandler.cs:152` infers `bool isGroupTransfer = recipients.Count > 1;`, misclassifying any 2-member group chat (`recipients.Count == 1`) as 1:1 (`DeliveryRoute.RelayedOneToOne` instead of `DeliveryRoute.RelayedGroup`).
  - **Persistence Gap & Concurrent Checkpoint Ordering Race:** `TransferSessionRuntime` mutates `LocalBitfield`, `UploadedBytes`, `State` (`InFlight -> Seeding/Completed`), and `CompletedAtUtc` exclusively in RAM. Because `SqlFileTransferQueryService` is strictly DB-driven, `ChatViewModel` sees 0% progress and stale `InFlight` even after download completion, and app restart loses verified chunk progress. Furthermore, multiple concurrent `PumpChannelAsync` peer tasks in a swarm must serialize checkpoint writes so an older async SQLite write can never overwrite a newer verified bitfield.
- **Exact Implementation Specification:**
  1. **Fix Filter Policy Owner-ID Lookup:**
     - In `Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferIngressFilter.cs`:
       - Line 32 (`EvaluateAsync`): change `context.SenderIdentityId` to `context.RecipientIdentityId` in `GetPolicyAsync(...)`. Keep `policy.EvaluatePeer(context.SenderIdentityId)`.
       - Line 46 (`Evaluate`): change `context.SenderIdentityId` to `context.RecipientIdentityId` in `GetPolicyAsync(...)`. Keep `policy.EvaluatePeer(context.SenderIdentityId)`.
     - In `Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferPayloadHandler.cs`:
       - Line 56 (`HandleInboundAsync`): change `context.SenderIdentityId` to `context.RecipientIdentityId` in `GetPolicyAsync(...)`. Keep `policy.EvaluatePeer(context.SenderIdentityId)`.
     - In `Percolator.Apps.FileTransfer.Tests/TestDoubles/TestDoubles.cs` (`InMemoryFilterPolicyRepository`):
       - Replace `public FileTransferFilterPolicy Policy { get; set; }` with `private readonly ConcurrentDictionary<PublicIdentityId, FileTransferFilterPolicy> _policies = new();`.
       - `SavePolicyAsync`: `_policies[policy.OwnerId] = policy;`.
       - `GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default)`: return `_policies.TryGetValue(ownerId, out var p) ? p : FileTransferFilterPolicy.DefaultPermissive(ownerId)`.
     - Update `Percolator.Apps.FileTransfer.Tests/Features/IngressControl/Tests/IngressControlTests.cs` (`UT_ING_02`, `UT_ING_03`) and `TransferSessionRuntimeTests.cs` (line 486) to save policies via `SavePolicyAsync` keyed to `recipientId` / `localId`, and add `UT_ING_09_PolicyLookupIsScopedToRecipientOwnerNotSender` verifying a policy stored under `senderId` does NOT drop messages to `recipientId`, while a policy stored under `recipientId` DOES drop `senderId` and blocked relays.
  2. **Propagate `SendPayloadAsync` Failures, Support 2-Member Group Transfers & Use `FileTransferControlFraming` in `FileTransferActionHandler`:**
     - In `Percolator.Apps.FileTransfer/Ports/IFileTransferActionHandler.cs` (lines 14–23) and `Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs` (lines 60–68):
       - Add optional parameter `bool isGroupTransfer = false` before `CancellationToken ct = default` on `InitiateTransferAsync`.
       - Replace line 152 (`bool isGroupTransfer = recipients.Count > 1;`) with `bool effectiveIsGroup = isGroupTransfer || recipients.Count > 1;` and use `effectiveIsGroup` throughout `InitiateTransferAsync`.
     - Replace all 4 duplicated `PrependOpcode` + `OutboundPayloadContext` blocks in `FileTransferActionHandler.cs` with `FileTransferControlFraming.FramePayload` / `FileTransferControlFraming.BuildOutboundContext` and delete private `PrependOpcode` (lines 685–691).
     - Inspect the `DomainResult` returned by EVERY `_payloadSender.SendPayloadAsync(...)` call:
       - `InitiateTransferAsync` (lines 250, 263): if `sendResult.IsFailure`, call `await _runtime.StopSessionAsync(sessionId, deleteCertificate: true, ct);` so no orphaned active runtime handle or certificate leaks, and return `DomainResult<TransferSessionId>.Failure(sendResult.Error)`.
       - `AcceptTransferAsync` (lines 412, 435): if `sendResult.IsFailure`, call `await _runtime.StopSessionAsync(sessionId, deleteCertificate: false, ct);` and return `sendResult`.
       - `ResumeTransferAsync` (lines 547, 571): if `sendResult.IsFailure`, return `sendResult`.
       - `CancelTransferAsync` (lines 652, 666): if `sendResult.IsFailure`, return `sendResult` (session is already persisted as `Cancelled` and stopped locally).
     - Add unit tests in `ActionHandlerAndQueryTests.cs`:
       - Verify `InitiateTransferAsync(..., isGroupTransfer: true)` with a single recipient creates `MultiPeer` topology and sends `DeliveryRoute.RelayedGroup` with `RecipientIdentityId == null`.
       - Verify `InitiateTransferAsync` propagates `_payloadSender` failure and stops the runtime handle (`runtime.HasActiveSession(...) == false`).
  3. **Add `ITransferSessionCheckpointStore` Port & SQLite Adapter:**
     - Create `Percolator.Apps.FileTransfer/Ports/ITransferSessionCheckpointStore.cs`:
       ```csharp
       using Percolator.Apps.FileTransfer.Domain.Common;
       using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

       namespace Percolator.Apps.FileTransfer.Ports;

       public interface ITransferSessionCheckpointStore
       {
           ValueTask SaveCheckpointAsync(
               TransferSessionId sessionId,
               Bitfield localBitfield,
               TransferSessionState state,
               TotalBundleBytes uploadedBytes,
               DateTimeOffset? completedAtUtc,
               CancellationToken ct = default);
       }
       ```
     - Create `Percolator.Infrastructure/Persistence/Repositories/SqliteTransferSessionCheckpointStore.cs` (singleton, injecting `IDbContextFactory<PercolatorDbContext>`):
       - Opens short-lived `PercolatorDbContext`; loads `TransferSessionEntity? session` by `sessionId.Value` — if `null`, returns without error.
       - Updates `session.State = (int)state`, `session.UploadedSizeBytes = uploadedBytes.Bytes`, `session.CompletedAtUtc = completedAtUtc`.
       - Upserts `TransferBitfieldEntity` for `sessionId.Value` (`BitfieldBytes = localBitfield.ToByteArray()`, `VerifiedPieces = localBitfield.PopCount`, `TotalPieces = localBitfield.TotalChunks.Count`).
       - Calls `await db.SaveChangesAsync(ct).ConfigureAwait(false)`.
     - Register `services.AddSingleton<ITransferSessionCheckpointStore, SqliteTransferSessionCheckpointStore>();` in `InfrastructureServiceCollectionExtensions.AddFileTransferInfrastructure()`.
  4. **Wire Serialized Checkpointing into `TransferSessionRuntime`:**
     - In `Percolator.Apps.FileTransfer/Ports/ITransferSessionRuntime.cs` (`TransferSessionRuntimeOptions`, lines 10–17):
       - Add optional parameters `TimeSpan? CheckpointInterval = null` (property `EffectiveCheckpointInterval => CheckpointInterval ?? TimeSpan.FromMilliseconds(500)`) and `int CheckpointChunkBatchSize = 16`.
     - In `Percolator.Apps.FileTransfer/Features/Runtime/TransferSessionRuntime.cs`:
       - Add required non-null constructor parameter `ITransferSessionCheckpointStore checkpointStore`.
       - On `ActiveTransferHandle`, add `public SemaphoreSlim CheckpointLock { get; } = new(1, 1);`, `public bool HasUnpersistedChanges { get; set; }`, `public int ChunksSinceCheckpoint { get; set; }`, and `public DateTimeOffset LastCheckpointUtc { get; set; }`.
       - Add helper `PersistCheckpointIfNeededAsync(ActiveTransferHandle handle, bool force, CancellationToken ct)`:
         1. Fast check under `lock (handle.SyncRoot)`: return early unless `(force && handle.HasUnpersistedChanges)` OR `handle.ChunksSinceCheckpoint >= _options.CheckpointChunkBatchSize` OR `(handle.HasUnpersistedChanges && (_clock.UtcNow - handle.LastCheckpointUtc) >= _options.EffectiveCheckpointInterval)`.
         2. `await handle.CheckpointLock.WaitAsync(ct).ConfigureAwait(false);` (guarantees strictly monotonic SQLite writes when multiple peer `PumpChannelAsync` tasks verify chunks concurrently).
         3. Inside `try { ... } finally { handle.CheckpointLock.Release(); }`: re-check the due condition and snapshot the latest `(Id, LocalBitfield, State, UploadedBytes, CompletedAtUtc)` under `lock (handle.SyncRoot)`, resetting `HasUnpersistedChanges = false`, `ChunksSinceCheckpoint = 0`, `LastCheckpointUtc = _clock.UtcNow`; then `await _checkpointStore.SaveCheckpointAsync(...)` while holding `CheckpointLock`. If `SaveCheckpointAsync` throws a non-cancellation exception, restore `HasUnpersistedChanges = true` under `lock (handle.SyncRoot)` so the next interval retries.
       - Call sites in `TransferSessionRuntime`:
         1. `PumpChannelAsync` after `HandleIncomingChunkDataAsync` succeeds: increment `ChunksSinceCheckpoint`, set `HasUnpersistedChanges = true`, and call `PersistCheckpointIfNeededAsync(handle, force: transitionedToSeeding || isTerminal, ct)`.
         2. `PumpChannelAsync` after `HandleRequestChunkAsync` serves a chunk: set `HasUnpersistedChanges = true`, and call `PersistCheckpointIfNeededAsync(handle, force: false, ct)`.
         3. `EvaluateSeedingRetentionAsync` when `stopped == true`: set `HasUnpersistedChanges = true` and call `PersistCheckpointIfNeededAsync(handle, force: true, ct)` prior to `StopSessionInternalAsync`.
         4. `StopSessionInternalAsync` and `StopAllSessionsAsync`: AFTER `WaitBoundedAsync(Task.WhenAll(handle.PeerPumpTasks.Values), boundedCt)` finishes, invoke `PersistCheckpointIfNeededAsync(handle, force: true, boundedCt)` (catching `OperationCanceledException`/transient storage exceptions so teardown never stalls), then call `handle.CheckpointLock.Dispose();` and `handle.SessionCts.Dispose();`.
     - Update test doubles & call sites:
       - Add `InMemoryTransferSessionCheckpointStore : ITransferSessionCheckpointStore` in `Percolator.Apps.FileTransfer.Tests/TestDoubles/TestDoubles.cs`.
       - Pass `ITransferSessionCheckpointStore` to all `new TransferSessionRuntime(...)` sites (`IngressControlTests.cs:35`, `TransferNegotiationTests.cs:44`, `ActionHandlerAndQueryTests.cs:47,309`, `TransferSessionRuntimeTests.cs:492`, plus DI registration in `TransferSessionRuntimeTests.cs:455`).
       - Add unit tests in `TransferSessionRuntimeTests.cs` verifying batched chunk checkpointing and immediate checkpoint on `InFlight -> Seeding` transition, a SQLite round-trip test in `SqliteTransferSessionRepositoryTests.cs` for `SqliteTransferSessionCheckpointStore`, and an assertion in `FileTransferEndToEndIntegrationTests.cs` confirming `IFileTransferQueryService.GetTransferByIdAsync(sessionId)` reports `Seeding`/`Completed` and 100% verified chunks in SQLite after download completion.
- **Acceptance Criteria:**
  - `FileTransferIngressFilter` and `FileTransferPayloadHandler` query policies strictly by `context.RecipientIdentityId` and evaluate against `context.SenderIdentityId`.
  - `InMemoryFilterPolicyRepository` keys policies strictly by `OwnerId`.
  - Every `_payloadSender.SendPayloadAsync` call in `FileTransferActionHandler` checks and propagates failures (with runtime cleanup on `InitiateTransferAsync` / `AcceptTransferAsync` send failure); 2-member group chats can be initiated via `isGroupTransfer: true`.
  - `TransferSessionRuntime` persists verified bitfield progress, uploaded bytes, and terminal/seeding state transitions monotonically via `ITransferSessionCheckpointStore` serialized by `CheckpointLock`.
  - `SqlFileTransferQueryService` reflects live chunk progress and `Seeding`/`Completed` status from SQLite without reading runtime RAM.
  - All test suites (`Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.Desktop.Tests`, `Percolator.Simulator.Tests`, `Percolator.ApplicationIntegrationTests`) pass 100%.

---

#### Ticket `FT-011` — Hot-Path Concurrency Correctness, `O(1)` Scheduling & Zero-Copy Buffer Pooling
- **Layer / Scope:** `Percolator.Apps.FileTransfer.Domain`, `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Apps.FileTransfer.Domain.Tests`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`
- **Dependencies:** `FT-010`
- **Architectural Defects Addressed:**
  - **Split-Lock Data Race on `TransferSession`:** `TransferSessionRuntime` locks `handle.SyncRoot`, `TransferSessionCoordinator` locks a separate private `_sessionSync`, and `DiskChunkAssembler.AssembleChunkAsync` calls `session.VerifyAndRecordChunk(...)` with **no lock at all** — AND flips `LocalBitfield` before writing to disk (allowing concurrent uploaders to read unwritten zeros).
  - **Quadratic Bitfield & Required-Mask Allocations:** `TransferSessionRuntime.ScheduleAndRequestMissingChunksAsync` recomputes `Manifest.GetRequiredChunks(SelectedFiles)` (which clones the entire `byte[]` on every bit set), copies `Scheduler.InFlightChunks` into a new `HashSet` per iteration, and runs an `O(TotalChunks × ActivePeers)` linear scan under `lock (handle.SyncRoot)`. `ApplyResumptionBitfield` and `ScanAndComputeBitfieldAsync` also call `bitfield.WithChunk` inside a chunk loop (`O(C² / 8)` allocations). `SelectedFileIndices.All(50_000)` allocates a 50k-element `HashSet`.
  - **Unbounded `_domainEvents` Memory Leak:** `TransferSession.VerifyAndRecordChunk` appends `ChunkVerifiedDomainEvent` and `FileCompletedDomainEvent` on every chunk, and `TransferSessionRuntime` never clears `_domainEvents` (leaking 500k+ objects per large transfer).
  - **Redundant Binary Search & LOH Churn:** `ChunkBoundaryCalculator.CalculateSlicesFromEntries` binary-searches on every tiny file inside a chunk (~350 binary searches per 1 MB chunk in `node_modules`) and is called twice per received chunk. `TransferSessionCoordinator.HandleRequestChunkAsync` (line 126) allocates `new byte[length]` defeating `ArrayPool`; `SwarmFrameCodec.DecodeChunkData` (line 350) allocates a fresh LOH `byte[]` on every received chunk.
  - **`TokenBucketRateLimiter` Lock Contention & Leak:** Shares a single `_syncLock` between global download and upload, and never evicts stopped sessions from `_sessionCaps`/`_sessionDownloadBuckets`/`_sessionUploadBuckets`.
- **Exact Implementation Specification:**
  1. **Eliminate Quadratic Domain Allocations (`Percolator.Apps.FileTransfer.Domain`):**
     - In `Percolator.Apps.FileTransfer.Domain/TransferNegotiation/Bitfield.cs`:
       - Add `public sealed class BitfieldBuilder` (`private readonly byte[] _bytes;`, `Set(ChunkIndex index)`, `SetRange(int firstChunk, int lastChunk)`, `Build() -> Bitfield` computing `PopCount` once).
       - Add `public Bitfield Union(Bitfield other)` (single-pass bitwise OR returning one `Bitfield`).
       - Add `public bool HasAnyBitIn(Bitfield requiredMask, Bitfield excludeMask)` checking `(_bytes[i] & requiredMask._bytes[i] & ~excludeMask._bytes[i]) != 0` with zero allocations.
       - Add `internal ReadOnlySpan<byte> AsSpan() => _bytes;` (for fast word-level scanning in domain/app selectors without cloning).
     - In `Percolator.Apps.FileTransfer.Domain/TransferNegotiation/SelectedFileIndices.cs`:
       - Represent `SelectedFileIndices.All(FileCount totalFiles)` in `O(1)` memory (`public bool IsAll { get; }`, `private readonly int _totalFileCount;`, `private readonly HashSet<FileIndex>? _indices;`).
       - `Contains(FileIndex index)`: when `IsAll`, return `index.Value >= 0 && index.Value < _totalFileCount`; `SelectedCount`: return `IsAll ? _totalFileCount : _indices!.Count`.
       - `Indices`: when `IsAll`, lazily materialize or return a lightweight `IReadOnlySet<FileIndex>` backed by `[0.._totalFileCount)` so existing callers/tests iterating `Indices` remain 100% compatible without allocating a 50k `HashSet` on creation.
     - In `Percolator.Apps.FileTransfer.Domain/ManifestManagement/TransferManifest.cs` (`GetRequiredChunks`, lines 118–137):
       - Fast-path `if (selectedFiles.IsAll || selectedFiles.SelectedCount == Files.Count) return Bitfield.Full(TotalChunks);`.
       - For partial selections, use `BitfieldBuilder` (`builder.SetRange(firstChunk, lastChunk)`) -> single `Build()`.
     - In `Percolator.Apps.FileTransfer.Domain/ManifestManagement/ChunkBoundaryCalculator.cs` (lines 16–47):
       - Binary-search ONCE via `FindFirstFileIndexForOffset(files, chunkStart)` returning `int fileIdx`, then advance `fileIdx++` linearly across contiguous non-empty files until `remainingChunkBytes.Bytes == 0`.
     - In `Percolator.Apps.FileTransfer.Domain/TransferNegotiation/TransferSession.cs`:
       - Cache `private Bitfield? _cachedRequiredChunks;` exposed via `public Bitfield RequiredChunksMask => _cachedRequiredChunks ??= (SelectedFiles != null ? Manifest.GetRequiredChunks(SelectedFiles) : Bitfield.Full(Manifest.TotalChunks));`; invalidate `_cachedRequiredChunks = null;` whenever `SelectedFiles` changes (`AcceptTransfer`).
       - Update `IsDownloadComplete` (line 30) and `GetMissingRequiredChunks()` (line 188) to use `RequiredChunksMask`.
       - Update `UpdatePeerInterestAndCheckStallRecovery` (line 496) and `ReevaluateAllPeerInterests` (line 511) to use `member.Bitfield.HasAnyBitIn(RequiredChunksMask, LocalBitfield)` (zero allocations!).
       - Update `ApplyResumptionBitfield` (lines 598–605) to `LocalBitfield = LocalBitfield.Union(scannedBitfield);` (single allocation instead of O(C²)).
       - Add `public DomainResult<IReadOnlyList<FileEntry>> RecordPreVerifiedChunk(ChunkIndex chunkIndex, IReadOnlyList<FileChunkSlice> slices, IDateTimeProvider clock)`:
         - Assumes Merkle hash was already verified off-lock; validates `State == InFlight` and chunk bounds; if `LocalBitfield.HasChunk(chunkIndex)` returns `Success([])`; flips `LocalBitfield`, calls `ReevaluateAllPeerInterests()`, raises `ChunkVerifiedDomainEvent`, inspects the passed `slices` (no second `CalculateSlices` call!) collecting completed `FileEntry` items + raising `FileCompletedDomainEvent`, handles `IsDownloadComplete -> Seeding/Completed`, and returns `newlyCompletedFiles`. Keep `VerifyAndRecordChunk` calling `VerifyLeaf` + `CalculateSlices` + `RecordPreVerifiedChunk` so all existing domain unit tests pass unchanged.
  2. **Unify Session Locking, Off-Lock Hash Verification & Write-Before-Bitfield Order:**
     - In `Percolator.Apps.FileTransfer/Features/Storage/DiskChunkAssembler.cs`:
       - Add optional `object? sessionSync = null` parameter to `AssembleChunkAsync`:
         1. Off-lock: check chunk bounds, expected byte length, and `session.Manifest.MerkleTree.VerifyLeaf(chunkIndex, plaintextData.Span)` (if mismatch -> return `Transfer.CryptographicVerificationFailed` without touching disk!).
         2. Compute `IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(...)` ONCE.
         3. Write verified slices to disk (outside `sessionSync`, so concurrent peers/transfers never block on disk I/O while holding the session lock).
         4. Acquire `lock (sync)` ONLY for the fast memory commit: `DomainResult<IReadOnlyList<FileEntry>> commitResult = session.RecordPreVerifiedChunk(chunkIndex, slices, clock);` (guarantees peers can never read unwritten bytes from disk after seeing `LocalBitfield` / `HaveFrame`!).
         5. Outside `lock (sync)`: call `_fileStorage.FinalizeFileAsync(...)` for each entry in `commitResult.Value`.
     - In `Percolator.Apps.FileTransfer/Features/MultiStreamEngine/TransferSessionCoordinator.cs`:
       - Accept optional `object? sessionSync = null` in constructor (`_sessionSync = sessionSync ?? new object();`).
       - Pass `_sessionSync` into `_assembler.AssembleChunkAsync(..., _sessionSync, ct)`.
       - In `TransferSessionRuntime.CreateHandle`: pass `handle.SyncRoot` into `new TransferSessionCoordinator(scheduler, assembler, _clock, _rateLimiter, handle.SyncRoot)` so `Runtime`, `Coordinator`, and `DiskChunkAssembler` share ONE lock.
       - In `TransferSessionRuntime` after `HandleIncomingChunkDataAsync` succeeds: call `handle.Session.ClearDomainEvents();` under `lock (handle.SyncRoot)` so `_domainEvents` stays bounded at `O(1)`.
  3. **Incremental Piece Availability Index & Allocation-Free Scheduling:**
     - Create `Percolator.Apps.FileTransfer/Features/SwarmCoordination/PieceAvailabilityIndex.cs`:
       - Holds `int[] _availability` (length `totalChunks`) and `Dictionary<PublicIdentityId, Bitfield> _peerBitfields`.
       - Methods (called under `handle.SyncRoot`): `SetPeerBitfield(PublicIdentityId peerId, Bitfield newBitfield)`, `RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex)`, `RemovePeer(PublicIdentityId peerId)`, and `ChunkIndex? SelectRarestForPeer(PublicIdentityId peerId, Bitfield localBitfield, Bitfield requiredMask, Func<ChunkIndex, bool> isInFlight)`.
       - `SelectRarestForPeer` scans `requiredMask`, `localBitfield`, and `peerBitfield` byte-by-byte (using `byte candidateMask = (byte)(req & ~loc & peer); if (candidateMask == 0) continue;`, iterating set bits via `BitOperations.LeadingZeroCount`) — skipping 8 already-owned/unavailable chunks per iteration and looking up `_availability[chunkIdx]` in **O(1)** instead of looping all peers per chunk!
     - In `Percolator.Apps.FileTransfer/Features/MultiStreamEngine/ChunkPipelineScheduler.cs`:
       - Keep `InFlightChunks` for existing tests, and rely on `IsChunkInFlight(ChunkIndex)` (or `TryScheduleRarest(PublicIdentityId peerId, Func<Func<ChunkIndex, bool>, ChunkIndex?> selector)` holding `_sync` cleanly without allocating a `HashSet` copy per request!).
     - Wire `PieceAvailabilityIndex` into `ActiveTransferHandle` in `TransferSessionRuntime.cs` (update on `BitfieldFrame`, `HaveFrame`, `RegisterSwarmMember`, peer disconnect, and peer eviction; call from `ScheduleAndRequestMissingChunksAsync`).
  4. **Zero-Copy Pooled Buffers & `TokenBucketRateLimiter` Hygiene:**
     - In `Percolator.Apps.FileTransfer/Features/MultiStreamEngine/StreamFramePayloads.cs`:
       - On `ChunkDataFrame`, add optional `IDisposable? BufferOwner = null` (excluded from value equality or implemented with `public void ReleaseBuffer() => BufferOwner?.Dispose();`).
     - In `Percolator.Apps.FileTransfer/Ports/IDesktopFileStorage.cs`:
       - Add default-implemented zero-copy read methods (overridden by `PortableDesktopFileStore` in `FT-012`):
         ```csharp
         async ValueTask<int> ReadChunkIntoAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, Memory<byte> destination, CancellationToken ct = default)
         {
             PlaintextChunkData data = await ReadChunkAsync(sourcePath, fileOffset, TransferByteLength.FromBytes(destination.Length), ct).ConfigureAwait(false);
             data.Span.CopyTo(destination.Span);
             return data.Length;
         }
         ```
     - In `Percolator.Apps.FileTransfer/Features/MultiStreamEngine/TransferSessionCoordinator.cs` (`HandleRequestChunkAsync`, lines 106–130):
       - Use `ReadChunkIntoAsync` directly into `rentedPlaintext.AsMemory(slice.BufferOffset, slice.SliceLength.Bytes)` (falling back to `ReadBoundarySliceAsync` for unselected boundary slices).
       - Delete `byte[] chunkBuffer = new byte[length];` (line 126)! Construct `PlaintextChunkData.FromMemory(rentedPlaintext.AsMemory(0, length))` directly and `await stream.SendFrameAsync(new ChunkDataFrame(chunkIdx, plaintextChunk), ct);` while `rentedPlaintext` stays rented until `finally` returns it.
     - In `Percolator.Infrastructure/Plugins/FileTransfer/SwarmFrameCodec.cs` (`DecodeChunkData`, line 350):
       - Rent `IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(dataLen);`, copy `payload.Slice(4)` into `owner.Memory.Span.Slice(0, dataLen)`, and return `new ChunkDataFrame(ChunkIndex.FromInt(chunkIndex), PlaintextChunkData.FromMemory(owner.Memory.Slice(0, dataLen)), owner)`.
       - In `TransferSessionRuntime` (and `TransferSessionCoordinator.PumpSingleStreamAsync`): wrap `ChunkDataFrame` processing in `try { ... } finally { chunkData.ReleaseBuffer(); }`.
     - In `Percolator.Infrastructure/Plugins/FileTransfer/PortableDesktopFileStore.cs` (`ScanAndComputeBitfieldAsync`, lines 291–382):
       - Replace `bitfield = bitfield.WithChunk(chunkIndex)` with `BitfieldBuilder` (`builder.Set(chunkIndex)` -> `builder.Build()`).
     - In `Percolator.Apps.FileTransfer/Ports/ITransferRateLimiter.cs` & `Percolator.Infrastructure/Plugins/FileTransfer/TokenBucketRateLimiter.cs`:
       - Add `void RemoveSession(TransferSessionId sessionId) { }` (default empty for stubs); in `TokenBucketRateLimiter`, split `_syncLock` into `_globalDownloadLock` and `_globalUploadLock`, and implement `RemoveSession` to remove entries from `_sessionCaps`, `_sessionDownloadBuckets`, and `_sessionUploadBuckets` (disposing bucket `SemaphoreSlim`s). Call `_rateLimiter.RemoveSession(sessionId)` in `TransferSessionRuntime.StopSessionInternalAsync` and `StopAllSessionsAsync`.
  5. **Tests to Add / Update:**
     - `Percolator.Apps.FileTransfer.Domain.Tests`: test `BitfieldBuilder`, `Bitfield.Union`, `Bitfield.HasAnyBitIn`, `SelectedFileIndices.All(50_000)` O(1) creation, `ChunkBoundaryCalculator` spanning 500 tiny files + 0-byte files in one chunk, and `RecordPreVerifiedChunk`.
     - `Percolator.Apps.FileTransfer.Tests`: test `PieceAvailabilityIndex` rarest-first selection & peer removal, concurrent 4-peer chunk verification + upload against a single `TransferSessionRuntime` verifying zero lost bits / zero unwritten reads / `session.DomainEvents.Count == 0`, and `TokenBucketRateLimiter.RemoveSession`.
- **Acceptance Criteria:**
  - Single lock (`handle.SyncRoot`) shared across `TransferSessionRuntime`, `TransferSessionCoordinator`, and `DiskChunkAssembler`; SHA-256 verification and disk writes run off-lock; `LocalBitfield` flips only after disk write succeeds.
  - Zero `Bitfield.WithChunk` loops in `GetRequiredChunks`, `ApplyResumptionBitfield`, and `ScanAndComputeBitfieldAsync`; zero `new HashSet` copies per request in `ScheduleAndRequestMissingChunksAsync`; zero `new byte[length]` chunk copies in `HandleRequestChunkAsync` or `SwarmFrameCodec.DecodeChunkData`.
  - `_domainEvents` never accumulates unboundedly on active sessions in `TransferSessionRuntime`.
  - All test suites pass 100%.

---

#### Ticket `FT-012` — Concurrent Multi-Transfer Pipeline, `SafeFileHandle` LRU Storage Engine & WebRTC 64 KB Block Sub-Framing
- **Layer / Scope:** `Percolator.Apps.FileTransfer`, `Percolator.Infrastructure`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.ApplicationIntegrationTests`
- **Dependencies:** `FT-011`
- **Architectural Defects Addressed:**
  - **Single-Loop Full-Duplex Head-of-Line Blocking:** `TransferSessionRuntime.PumpChannelAsync` runs inbound receive, disk write, sequential `BroadcastHaveAsync` to all peers, and outbound disk read + send in one loop per peer. One slow peer or slow disk write stalls the entire swarm and triggers `WebRtcDataChannelAdapter` queue overflow disconnects.
  - **Unbounded Multi-Transfer Memory & Disk Thrashing:** `WebRtcDataChannelAdapter` uses a fixed 128-frame queue (up to 512 MB RAM per peer at 4 MB chunks -> 8 GB RAM across 4 concurrent 4-peer transfers), and there is no global cap on concurrent disk/hash workers across simultaneous transfers.
  - **WebRTC SCTP Oversized-Message Cliff:** `WebRtcPeerConnectionAdapter` (line 86) sets `MaxMessageSize = 4 MB + 64` and sends entire 1–4 MB chunks as single SCTP messages, exceeding standard 64 KB–256 KB WebRTC SCTP limits and causing WAN head-of-line stalls.
  - **`node_modules` Syscall & Initiation Collapse:** `IngressTransferOfferValidator.MaxFilesPerBundle` caps at 50,000; `PortableDesktopFileStore` opens/seeks/flushes/closes a new `FileStream` on every file slice (~350k syscalls for 45k tiny files); `AcceptTransferAsync` runs upfront 45k-file `ScanAndComputeBitfieldAsync` + synchronous `PreAllocateFilesAsync` on brand-new folders; `InitiateTransferAsync` hashes chunk-by-chunk single-threaded, re-opening boundary files across chunks.
- **Exact Implementation Specification:**
  1. **WebRTC 64 KB SCTP Sub-Framing & Byte-Budgeted Pooled Reassembly:**
     - In `Percolator.Infrastructure/Plugins/FileTransfer/WebRtc/WebRtcPeerConnectionAdapter.cs` (line 86):
       - Change `MaxMessageSize` to `WebRtcDataChannelAdapter.MaxSctpMessageBytes` (`65_536`).
     - In `Percolator.Infrastructure/Plugins/FileTransfer/WebRtc/WebRtcDataChannelAdapter.cs`:
       - Define constants:
         - `internal const int MaxSctpMessageBytes = 65_536;`
         - `private const byte FragmentOpcode = 0xFF;` (disjoint from `StreamFrameType` `0x01..0x0C`)
         - `private const int FragmentHeaderBytes = 13;` (`[0xFF (1B) | uint32 msgId (4B) | uint32 totalLen (4B) | uint32 offset (4B)]`)
         - `private const int MaxFragmentPayloadBytes = MaxSctpMessageBytes - FragmentHeaderBytes;` (`65_523`)
         - `private const int MaxConcurrentReassemblies = 4;`
         - `private const long MaxBufferedInboundBytes = 8L * 1024 * 1024;` (8 MB cap per channel instead of 128 × 4 MB = 512 MB!)
       - Replace `Channel<byte[]> _inboundFrames` with `Channel<PooledWireFrame> _inboundFrames` (where `PooledWireFrame` holds `byte[] RentedBuffer, int Length` and implements `IDisposable` returning to `ArrayPool<byte>.Shared` + decrementing `Interlocked.Add(ref _bufferedInboundBytes, -Length)`).
       - `SendFrameAsync`:
         - If `encoded.Length <= MaxSctpMessageBytes`, send directly via `_channel.Send(encoded.Span)`.
         - Else allocate monotonic `uint msgId = Interlocked.Increment(ref _nextFragmentMsgId);`, rent a single `MaxSctpMessageBytes` scratch buffer from `ArrayPool<byte>.Shared`, and under `_sendLock` send consecutive slices of size `Math.Min(MaxFragmentPayloadBytes, encoded.Length - offset)` with the 13-byte header (`FragmentOpcode`, `msgId`, `encoded.Length`, `offset`), returning the scratch buffer in `finally`.
       - `OnBinaryReceived`:
         - If `data.Length > 0 && data[0] == FragmentOpcode`: validate `data.Length > FragmentHeaderBytes`, parse `(msgId, totalLen, offset)`, validate `totalLen >= SwarmFrameCodec.FrameHeaderSizeBytes && totalLen <= SwarmFrameCodec.MaxWireFrameSizeBytes`, validate `offset + payloadLen <= totalLen`, reject if active reassemblies exceed `MaxConcurrentReassemblies` (or duplicate/overlapping range exceeds `totalLen`), copy fragment payload directly into the single rented `ArrayPool` buffer for `msgId`. When `receivedBytes == totalLen`, remove from `_reassemblies` and enqueue as `PooledWireFrame`.
         - Else (unfragmented frame): rent `ArrayPool<byte>.Shared.Rent(data.Length)`, copy `data`, enqueue as `PooledWireFrame`.
         - Enforce `MaxBufferedInboundBytes` via `Interlocked.Add(ref _bufferedInboundBytes, frame.Length)`: if exceeded, return rented buffer, fail channel cleanly, and dispose off-thread.
       - In `ReceiveFrameAsync`: `using PooledWireFrame rawFrame = await _inboundFrames.Reader.ReadAsync(ct); return SwarmFrameCodec.DecodeFramed(rawFrame.Span);` (returns the rented wire buffer immediately after decode!).
       - In `DisposeAsync`: drain remaining queued `PooledWireFrame`s and `_reassemblies` buffers back to `ArrayPool<byte>.Shared` so zero pooled buffers leak on teardown.
  2. **Decoupled Full-Duplex Peer Pipeline with Global Disk/Hash Concurrency Gate:**
     - In `Percolator.Apps.FileTransfer/Ports/ITransferSessionRuntime.cs` (`TransferSessionRuntimeOptions`):
       - Add optional parameters `int MaxConcurrentIoWorkers = 6` and `int OutboundPeerQueueCapacity = 64`.
     - In `Percolator.Apps.FileTransfer/Features/Runtime/TransferSessionRuntime.cs`:
       - Add runtime-wide `private readonly SemaphoreSlim _globalIoWorkerGate;` initialized to `Math.Max(1, _options.MaxConcurrentIoWorkers)` (shared across ALL simultaneous transfers on this node; disposed in `DisposeAsync()`).
       - Per attached peer channel (`PeerChannelPipe` stored in `handle.PeerPipes`), run TWO lightweight coordinated tasks:
         - **Writer Task:** reads from bounded `Channel<IStreamFramePayload>` (`OutboundQueue`, capacity `_options.OutboundPeerQueueCapacity`, `SingleReader = true`) and calls `await channel.SendFrameAsync(frame, ct)` (calling `ReleaseBuffer()` on `ChunkDataFrame` if applicable). `BroadcastHaveAsync` becomes non-blocking `pipe.OutboundQueue.Writer.TryWrite(haveFrame)` — a slow peer never blocks the swarm!
         - **Reader Task:** continuously reads `channel.ReceiveFrameAsync(ct)`; handles lightweight control frames (`BitfieldFrame`, `HaveFrame`, `InterestedFrame`, `NotInterestedFrame`, `UnchokeFrame`, `ChokeFrame`, `CancelChunkFrame`, `KeepAliveFrame`) inline; dispatches `ChunkDataFrame` (verify + disk write + checkpoint) and `RequestChunkFrame` (disk read + enqueue reply onto `pipe.OutboundQueue`) via bounded worker tasks gated by `_globalIoWorkerGate.WaitAsync(ct)` (and tracked in `handle.PeerPumpTasks` for bounded teardown).
         - Wait on `ChunkDataFrame`: ensure `chunkData.ReleaseBuffer()` ALWAYS runs in `finally` even on cancellation or failure.
  3. **`SafeFileHandle` LRU Handle Cache + `RandomAccess` + Lazy Allocation in `PortableDesktopFileStore`:**
     - In `Percolator.Infrastructure/Plugins/FileTransfer/PortableDesktopFileStore.cs`:
       - Implement `IDisposable` and add an internal thread-safe LRU open-handle pool (`MaxOpenFileHandles = 256`, configurable via optional constructor parameter `int maxOpenHandles = 256`).
       - Cache entry: `(SafeFileHandle Handle, int RefCount, bool MarkedForEviction, LinkedListNode<HandleKey> LruNode)` keyed by `(string NormalizedPath, FileAccess Access)` using `File.OpenHandle(path, mode, access, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.RandomAccess)`.
       - `AcquireHandle(path, mode, access)` increments `RefCount` and moves to MRU; `ReleaseHandle(entry)` decrements `RefCount` and disposes `SafeFileHandle` only when `MarkedForEviction && RefCount == 0` (evicting LRU entries when cache exceeds `maxOpenHandles`).
       - Add `EvictPath(string path)` (closes/disposes cached handles for both `path` and `GetPartFilePath(path)`, waiting briefly if `RefCount > 0`) called immediately at the start of `FinalizeFileAsync` before `File.Move(partPath, targetPath)` so Windows never throws sharing violations on rename.
       - Add a `ConcurrentDictionary<string, byte> _createdDirectories` cache in `EnsureParentDirectoryExists` so repeated slices into the same directory avoid redundant `Directory.Exists` syscalls.
       - Refactor `WriteChunkAsync`: acquire cached write `SafeFileHandle` for `.percolator-part` (creating lazily on first write if absent — setting length via `RandomAccess.SetLength` only when file is created for large files), call `await RandomAccess.WriteAsync(handle, data.Memory, fileOffset.Offset, ct).ConfigureAwait(false)` — **remove per-slice `FlushAsync()`**!
       - Implement `ReadChunkIntoAsync` (and refactor `ReadChunkAsync` to delegate to it): acquire cached read `SafeFileHandle` and call `await RandomAccess.ReadAsync(handle, destination, fileOffset.Offset, ct).ConfigureAwait(false)` in a loop until full or EOF.
       - Refactor `PreAllocateFilesAsync`: create 0-byte files immediately, and pre-allocate `.percolator-part` files ONLY for files `>= 1 * 1024 * 1024` (1 MB) so 50,000 tiny `node_modules` files are created lazily on first chunk write instead of blocking `AcceptTransferAsync` with 50,000 upfront `CreateFile` + `SetLength` syscalls.
       - Add `ValueTask<bool> HasAnyExistingTransferFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default)` on `IDesktopFileStorage` (default returns `true` for test doubles): `PortableDesktopFileStore` returns `false` immediately if `!Directory.Exists(rootPath.Value)` or `Directory.EnumerateFileSystemEntries(rootPath.Value)` is empty.
       - In `FileTransferActionHandler.AcceptTransferAsync` (line 308): call `HasAnyExistingTransferFilesAsync`; if `false`, skip `ScanAndComputeBitfieldAsync` and use `Bitfield.Empty(session.Manifest.TotalChunks)` directly!
       - Register `PortableDesktopFileStore` as `Singleton` (already singleton) and ensure `Dispose()` runs when the Stage-2 DI container disposes.
  4. **File-Sequential Parallel Hashing & Raised `MaxFilesPerBundle` (`node_modules` Initiation):**
     - In `Percolator.Apps.FileTransfer/Features/IngressControl/IngressTransferOfferValidator.cs` (line 13): raise `MaxFilesPerBundle` from `50_000` to `250_000`.
     - Create `Percolator.Apps.FileTransfer/Features/TransferNegotiation/TransferPreparationProgress.cs`:
       `public readonly record struct TransferPreparationProgress(int FilesScanned, int TotalFiles, int ChunksHashed, int TotalChunks);`
     - In `Percolator.Apps.FileTransfer/Ports/IFileTransferActionHandler.cs` & `FileTransferActionHandler.cs` (`InitiateTransferAsync`):
       - Add optional `IProgress<TransferPreparationProgress>? preparationProgress = null` parameter before `CancellationToken ct = default`.
       - Replace the chunk-by-chunk loop (`FileTransferActionHandler.cs:111-140`) with a **file-sequential streaming reader + bounded parallel SHA-256 pipeline**:
         - Open each non-empty file ONCE sequentially via `_fileStorage.ReadChunkIntoAsync` (in `chunkSize.Bytes` slices or file remainder), filling rented `ArrayPool<byte>.Shared.Rent(chunkSize.Bytes)` chunk buffers across file boundaries without re-opening boundary files.
         - Dispatch each completed chunk buffer to a bounded parallel hashing stage (`MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)`, bounded in-flight buffer count = `maxWorkers * 2` so hashing RAM stays <= `16 × chunkSize`) computing `MerkleTreeBuilder.HashLeaf(buf.AsSpan(0, expectedBytes))`, storing into `ChunkHash[] leafHashes` at `chunkIdx`, returning the buffer with `clearArray: true`, and reporting `preparationProgress?.Report(...)`.
         - Use `.ConfigureAwait(false)` throughout so Desktop UI threads are never blocked on hashing continuations.
  5. **Tests to Add / Verify:**
     - `Percolator.Infrastructure.Tests/Plugins/FileTransfer/WebRtcPeerTransportTests.cs`: add loopback test transferring a **2 MB single chunk** (>64 KB `MaxSctpMessageBytes`) over live `WebRtcDataChannelAdapter` verifying transparent 64 KB SCTP fragmentation, reassembly, exact byte equality, and zero leaked buffers on close; add test rejecting forged/oversized `0xFF` fragment headers.
     - `Percolator.Infrastructure.Tests/Plugins/FileTransfer/PortableDesktopFileStoreTests.cs`: test LRU eviction under `maxOpenHandles: 8` writing & finalizing **500 small files** concurrently (verifying zero Windows sharing violations on `FinalizeFileAsync` rename and exact byte integrity across all 500 files), plus `HasAnyExistingTransferFilesAsync` fast-path on empty folder.
     - `Percolator.Apps.FileTransfer.Tests/Features/TransferNegotiation/Tests/TransferNegotiationTests.cs`: verify file-sequential parallel hashing in `InitiateTransferAsync` produces byte-identical `MerkleRoot` and leaves across multi-file boundary-spanning bundles (including 0-byte files and 1,000 tiny files) and reports `TransferPreparationProgress`.
     - `Percolator.ApplicationIntegrationTests/Scenarios/FileTransferEndToEndIntegrationTests.cs`: add a concurrent stress test running **3 simultaneous transfers** (including one multi-hundred-file bundle) over live WebRTC + SQLite checkpointing, verifying 100% SHA-256 integrity, zero `SQLITE_BUSY` errors, and clean teardown.
- **Acceptance Criteria:**
  - `WebRtcPeerConnectionAdapter` uses `MaxMessageSize = 65_536`; 1–4 MB chunks transfer reliably over WebRTC via transparent 64 KB SCTP sub-framing with bounded 8 MB/channel receive memory.
  - `TransferSessionRuntime` separates per-peer reader/writer loops and caps global disk/hash concurrency via `_globalIoWorkerGate`; slow peers never stall `BroadcastHaveAsync`.
  - `PortableDesktopFileStore` uses `SafeFileHandle` LRU cache + `RandomAccess.ReadAsync`/`WriteAsync` (zero per-slice `FlushAsync`, lazy `.percolator-part` allocation for files `< 1 MB`, skipped initial scan on empty destination folders).
  - `InitiateTransferAsync` opens each file once and hashes chunks in bounded parallel with optional `IProgress<TransferPreparationProgress>`; `MaxFilesPerBundle = 250_000`.
  - All test suites (`Percolator.Apps.FileTransfer.Domain.Tests`, `Percolator.Apps.FileTransfer.Tests`, `Percolator.Infrastructure.Tests`, `Percolator.Desktop.Tests`, `Percolator.Simulator.Tests`, `Percolator.ApplicationIntegrationTests`) pass 100%.
