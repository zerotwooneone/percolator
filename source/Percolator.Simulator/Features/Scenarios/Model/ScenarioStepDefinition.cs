using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Percolator.Simulator.Features.Scenarios.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SpawnPersonasStep), "SpawnPersonas")]
[JsonDerivedType(typeof(InitiateDirectHandshakeStep), "InitiateDirectHandshake")]
[JsonDerivedType(typeof(ApprovePendingHandshakeStep), "ApprovePendingHandshake")]
[JsonDerivedType(typeof(RejectPendingHandshakeStep), "RejectPendingHandshake")]
[JsonDerivedType(typeof(SetListeningStateStep), "SetListeningState")]
[JsonDerivedType(typeof(SendDirectMessageStep), "SendDirectMessage")]
[JsonDerivedType(typeof(SpawnRelayStep), "SpawnRelay")]
[JsonDerivedType(typeof(RegisterMailboxesStep), "RegisterMailboxes")]
[JsonDerivedType(typeof(PublishPreKeysStep), "PublishPreKeys")]
[JsonDerivedType(typeof(InitiateRelayedHandshakeStep), "InitiateRelayedHandshake")]
[JsonDerivedType(typeof(DuplicateRelayEnvelopeStep), "DuplicateRelayEnvelope")]
[JsonDerivedType(typeof(FlipRelayEnvelopeBitStep), "FlipRelayEnvelopeBit")]
[JsonDerivedType(typeof(ShuffleRelayQueueStep), "ShuffleRelayQueue")]
[JsonDerivedType(typeof(AdvanceVirtualTimeStep), "AdvanceVirtualTime")]
[JsonDerivedType(typeof(SendDisappearingMessageStep), "SendDisappearingMessage")]
[JsonDerivedType(typeof(AddReactionStep), "AddReaction")]
[JsonDerivedType(typeof(EditMessageStep), "EditMessage")]
[JsonDerivedType(typeof(DeleteMessageStep), "DeleteMessage")]
[JsonDerivedType(typeof(MarkConversationReadStep), "MarkConversationRead")]
[JsonDerivedType(typeof(RerollProfileStep), "RerollProfile")]
[JsonDerivedType(typeof(CreateGroupStep), "CreateGroup")]
[JsonDerivedType(typeof(AcceptGroupInviteStep), "AcceptGroupInvite")]
[JsonDerivedType(typeof(DeclineGroupInviteStep), "DeclineGroupInvite")]
[JsonDerivedType(typeof(SendGroupMessageStep), "SendGroupMessage")]
[JsonDerivedType(typeof(FloodGroupMessagesStep), "FloodGroupMessages")]
[JsonDerivedType(typeof(AddGroupMemberStep), "AddGroupMember")]
[JsonDerivedType(typeof(EvictGroupMemberStep), "EvictGroupMember")]
[JsonDerivedType(typeof(RenameGroupStep), "RenameGroup")]
[JsonDerivedType(typeof(SetDiscoveryVisibilityStep), "SetDiscoveryVisibility")]
[JsonDerivedType(typeof(PublishBlindedLocatorStep), "PublishBlindedLocator")]
[JsonDerivedType(typeof(ResolveBlindedLocatorStep), "ResolveBlindedLocator")]
[JsonDerivedType(typeof(QueryNearbyPeersStep), "QueryNearbyPeers")]
[JsonDerivedType(typeof(ConnectDiscoveredPeerStep), "ConnectDiscoveredPeer")]
[JsonDerivedType(typeof(InitiateFileTransferStep), "InitiateFileTransfer")]
[JsonDerivedType(typeof(AcceptFileTransferStep), "AcceptFileTransfer")]
[JsonDerivedType(typeof(InjectLateDownloaderStep), "InjectLateDownloader")]
[JsonDerivedType(typeof(PauseFileTransferStep), "PauseFileTransfer")]
[JsonDerivedType(typeof(ResumeFileTransferStep), "ResumeFileTransfer")]
[JsonDerivedType(typeof(CancelFileTransferStep), "CancelFileTransfer")]
public abstract record ScenarioStepDefinition
{
    public abstract string DisplayText { get; }
    public override string ToString() => DisplayText;
}

public sealed record SpawnPersonasStep(IReadOnlyList<string> Aliases) : ScenarioStepDefinition
{
    public override string DisplayText => $"Spawn Personas: [{string.Join(", ", Aliases)}]";
}

public sealed record InitiateDirectHandshakeStep(string InitiatorAlias, string TargetAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Direct Handshake: '{InitiatorAlias}' ➔ '{TargetAlias}'";
}

public sealed record ApprovePendingHandshakeStep(string OwnerAlias, string SenderAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Approve Handshake: '{OwnerAlias}' accepts '{SenderAlias}'";
}

public sealed record RejectPendingHandshakeStep(string OwnerAlias, string SenderAlias, bool Block = false) : ScenarioStepDefinition
{
    public override string DisplayText => $"Reject Handshake: '{OwnerAlias}' rejects '{SenderAlias}' (Block: {Block})";
}

public sealed record SetListeningStateStep(string Alias, bool IsListening) : ScenarioStepDefinition
{
    public override string DisplayText => $"Set Listening: '{Alias}' ➔ {(IsListening ? "Listening" : "Muted")}";
}

public sealed record SendDirectMessageStep(string SenderAlias, string RecipientAlias, string Text) : ScenarioStepDefinition
{
    public override string DisplayText => $"Send Message: '{SenderAlias}' ➔ '{RecipientAlias}': \"{Text}\"";
}

// -----------------------------------------------------------------------------
// Phase 5 Scenario Steps: Relayed Chat, PreKeys & Queue Chaos
// -----------------------------------------------------------------------------

public sealed record SpawnRelayStep(string RelayAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Spawn Relay: '{RelayAlias}'";
}

public sealed record RegisterMailboxesStep(string RelayAlias, IReadOnlyList<string> PersonaAliases) : ScenarioStepDefinition
{
    public override string DisplayText => $"Register Mailboxes on '{RelayAlias}': [{string.Join(", ", PersonaAliases)}]";
}

public sealed record PublishPreKeysStep(string RelayAlias, IReadOnlyList<string> PersonaAliases, int OneTimeKeyCount = 25) : ScenarioStepDefinition
{
    public override string DisplayText => $"Publish {OneTimeKeyCount} PreKeys to '{RelayAlias}': [{string.Join(", ", PersonaAliases)}]";
}

public sealed record InitiateRelayedHandshakeStep(string InitiatorAlias, string TargetAlias, string RelayAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Relayed Handshake: '{InitiatorAlias}' ➔ '{TargetAlias}' via '{RelayAlias}'";
}

public sealed record DuplicateRelayEnvelopeStep(string RelayAlias, string? QueueId = null) : ScenarioStepDefinition
{
    public override string DisplayText => $"Duplicate Envelope on '{RelayAlias}'";
}

public sealed record FlipRelayEnvelopeBitStep(string RelayAlias, string? QueueId = null, int ByteOffset = 10) : ScenarioStepDefinition
{
    public override string DisplayText => $"Flip Bit (Offset {ByteOffset}) on '{RelayAlias}'";
}

public sealed record ShuffleRelayQueueStep(string RelayAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Shuffle Queue on '{RelayAlias}'";
}

// -----------------------------------------------------------------------------
// Phase 6A Scenario Steps: Virtual Clock, Disappearing Messages & Rich Chat
// -----------------------------------------------------------------------------

public sealed record AdvanceVirtualTimeStep(int Seconds) : ScenarioStepDefinition
{
    public override string DisplayText => $"Advance Virtual Time: +{Seconds}s";
}

public sealed record SendDisappearingMessageStep(string SenderAlias, string RecipientAlias, string Text, int ExpireAfterSeconds) : ScenarioStepDefinition
{
    public override string DisplayText => $"Send Disappearing Message: '{SenderAlias}' ➔ '{RecipientAlias}': \"{Text}\" (TTL: {ExpireAfterSeconds}s)";
}

public sealed record AddReactionStep(string SenderAlias, string RecipientAlias, string TargetMessageText, string Emoji) : ScenarioStepDefinition
{
    public override string DisplayText => $"Add Reaction: '{SenderAlias}' reacts '{Emoji}' to \"{TargetMessageText}\" on '{RecipientAlias}'";
}

public sealed record EditMessageStep(string SenderAlias, string RecipientAlias, string TargetMessageText, string NewText) : ScenarioStepDefinition
{
    public override string DisplayText => $"Edit Message: '{SenderAlias}' edits \"{TargetMessageText}\" ➔ \"{NewText}\" on '{RecipientAlias}'";
}

public sealed record DeleteMessageStep(string SenderAlias, string RecipientAlias, string TargetMessageText) : ScenarioStepDefinition
{
    public override string DisplayText => $"Delete Message: '{SenderAlias}' deletes \"{TargetMessageText}\" on '{RecipientAlias}'";
}

public sealed record MarkConversationReadStep(string OwnerAlias, string PeerAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Mark Conversation Read: '{OwnerAlias}' ⇄ '{PeerAlias}'";
}

// -----------------------------------------------------------------------------
// Phase 6B Scenario Steps: Profile Reroll
// -----------------------------------------------------------------------------

public sealed record RerollProfileStep(string PersonaAlias, string? NewDisplayName = null) : ScenarioStepDefinition
{
    public override string DisplayText => NewDisplayName != null
        ? $"Reroll Profile for '{PersonaAlias}' ➔ '{NewDisplayName}'"
        : $"Reroll Profile for '{PersonaAlias}'";
}

// -----------------------------------------------------------------------------
// Phase 7 Scenario Steps: Group Messaging, ZK Presentation Proofs & Epochs
// -----------------------------------------------------------------------------

public sealed record CreateGroupStep(string AdminAlias, string RelayAlias, string GroupName, IReadOnlyList<string> MemberAliases) : ScenarioStepDefinition
{
    public override string DisplayText => $"Create Group: '{GroupName}' on '{RelayAlias}' by '{AdminAlias}' with [{string.Join(", ", MemberAliases)}]";
}

public sealed record AcceptGroupInviteStep(string PersonaAlias, string GroupName) : ScenarioStepDefinition
{
    public override string DisplayText => $"Accept Group Invite: '{PersonaAlias}' joins '{GroupName}'";
}

public sealed record DeclineGroupInviteStep(string PersonaAlias, string GroupName) : ScenarioStepDefinition
{
    public override string DisplayText => $"Decline Group Invite: '{PersonaAlias}' declines '{GroupName}'";
}

public sealed record SendGroupMessageStep(string SenderAlias, string GroupName, string Text) : ScenarioStepDefinition
{
    public override string DisplayText => $"Send Group Message: '{SenderAlias}' in '{GroupName}': \"{Text}\"";
}

public sealed record FloodGroupMessagesStep(string SenderAlias, string GroupName, int Count = 25) : ScenarioStepDefinition
{
    public override string DisplayText => $"Flood {Count} Group Messages: '{SenderAlias}' in '{GroupName}'";
}

public sealed record AddGroupMemberStep(string AdminAlias, string GroupName, string NewMemberAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Add Group Member: '{AdminAlias}' adds '{NewMemberAlias}' to '{GroupName}'";
}

public sealed record EvictGroupMemberStep(string AdminAlias, string GroupName, string EvictedMemberAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Evict Group Member: '{AdminAlias}' evicts '{EvictedMemberAlias}' from '{GroupName}'";
}

public sealed record RenameGroupStep(string AdminAlias, string GroupName, string NewGroupName) : ScenarioStepDefinition
{
    public override string DisplayText => $"Rename Group: '{AdminAlias}' renames '{GroupName}' ➔ '{NewGroupName}'";
}

// -----------------------------------------------------------------------------
// Phase 8 Scenario Steps: Peer Discovery, Blinded Rendezvous & Epoch Rotation
// -----------------------------------------------------------------------------

public sealed record SetDiscoveryVisibilityStep(string PersonaAlias, bool IsDiscoverable) : ScenarioStepDefinition
{
    public override string DisplayText => $"Set Discovery Visibility: '{PersonaAlias}' ➔ {(IsDiscoverable ? "Discoverable" : "Hidden")}";
}

public sealed record PublishBlindedLocatorStep(string PersonaAlias, string RelayAlias, string SharedSecret, string? RouteDescriptor = null) : ScenarioStepDefinition
{
    public override string DisplayText => $"Publish Blinded Locator: '{PersonaAlias}' to '{RelayAlias}'";
}

public sealed record ResolveBlindedLocatorStep(string PersonaAlias, string RelayAlias, string TargetAlias, string SharedSecret) : ScenarioStepDefinition
{
    public override string DisplayText => $"Resolve Blinded Locator: '{PersonaAlias}' queries '{RelayAlias}' for '{TargetAlias}'";
}

public sealed record QueryNearbyPeersStep(string PersonaAlias, string RelayAlias, int Limit = 10) : ScenarioStepDefinition
{
    public override string DisplayText => $"Query Nearby Peers: '{PersonaAlias}' on '{RelayAlias}' (Limit: {Limit})";
}

public sealed record ConnectDiscoveredPeerStep(string InitiatorAlias, string CandidateAlias, string RelayAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Connect Discovered Peer: '{InitiatorAlias}' ➔ '{CandidateAlias}' via '{RelayAlias}'";
}

// -----------------------------------------------------------------------------
// Phase 9 Scenario Steps: Swarm File Transfer, Piece Heatmap & Late Downloader
// -----------------------------------------------------------------------------

public sealed record InitiateFileTransferStep(string InitiatorAlias, IReadOnlyList<string> RecipientAliases, string PresetSize = "2 MB") : ScenarioStepDefinition
{
    public override string DisplayText => $"Initiate File Transfer: '{InitiatorAlias}' ➔ [{string.Join(", ", RecipientAliases)}] ({PresetSize})";
}

public sealed record AcceptFileTransferStep(string RecipientAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Accept File Transfer: '{RecipientAlias}'";
}

public sealed record InjectLateDownloaderStep(string LateJoinerAlias, double ProgressThreshold = 0.5) : ScenarioStepDefinition
{
    public override string DisplayText => $"Inject Late Downloader: '{LateJoinerAlias}' at {ProgressThreshold:P0} threshold";
}

public sealed record PauseFileTransferStep(string PersonaAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Pause File Transfer: '{PersonaAlias}'";
}

public sealed record ResumeFileTransferStep(string PersonaAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Resume File Transfer: '{PersonaAlias}'";
}

public sealed record CancelFileTransferStep(string PersonaAlias) : ScenarioStepDefinition
{
    public override string DisplayText => $"Cancel File Transfer: '{PersonaAlias}'";
}
