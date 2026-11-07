using System.Text.Json.Serialization;

namespace Percolator.Simulator.Features.Scenarios.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AssertDirectRatchetSessionAssertion), "AssertDirectRatchetSession")]
[JsonDerivedType(typeof(AssertMessageDeliveredAssertion), "AssertMessageDelivered")]
[JsonDerivedType(typeof(AssertZeroErrorsAssertion), "AssertZeroErrors")]
[JsonDerivedType(typeof(AssertHostedOpkCountAssertion), "AssertHostedOpkCount")]
[JsonDerivedType(typeof(AssertMailboxQueueDepthAssertion), "AssertMailboxQueueDepth")]
[JsonDerivedType(typeof(AssertLogContainsCodeAssertion), "AssertLogContainsCode")]
[JsonDerivedType(typeof(AssertMessageExpiredAssertion), "AssertMessageExpired")]
[JsonDerivedType(typeof(AssertReactionPresentAssertion), "AssertReactionPresent")]
[JsonDerivedType(typeof(AssertMessageEditedAssertion), "AssertMessageEdited")]
[JsonDerivedType(typeof(AssertMessageTombstonedAssertion), "AssertMessageTombstoned")]
[JsonDerivedType(typeof(AssertMessageReadAssertion), "AssertMessageRead")]
[JsonDerivedType(typeof(AssertProfileSyncedAssertion), "AssertProfileSynced")]
[JsonDerivedType(typeof(AssertGroupEpochAssertion), "AssertGroupEpoch")]
[JsonDerivedType(typeof(AssertGroupMessageDeliveredAssertion), "AssertGroupMessageDelivered")]
[JsonDerivedType(typeof(AssertMemberCannotDecryptAssertion), "AssertMemberCannotDecrypt")]
public abstract record ScenarioAssertionDefinition
{
    public abstract string DisplayText { get; }
    public override string ToString() => DisplayText;
}

public sealed record AssertDirectRatchetSessionAssertion(string Alias1, string Alias2) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Ratchet Session: '{Alias1}' ⇄ '{Alias2}'";
}

public sealed record AssertMessageDeliveredAssertion(string RecipientAlias, string ExpectedText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Message Delivered: '{RecipientAlias}' received \"{ExpectedText}\"";
}

public sealed record AssertZeroErrorsAssertion() : ScenarioAssertionDefinition
{
    public override string DisplayText => "Assert Zero Errors in Session Logs";
}

// -----------------------------------------------------------------------------
// Phase 5 Scenario Assertions: Hosted PreKeys, Queue Depth & Log Verification
// -----------------------------------------------------------------------------

public sealed record AssertHostedOpkCountAssertion(string RelayAlias, string PersonaAlias, int ExpectedCount) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Hosted OPK Count on '{RelayAlias}' for '{PersonaAlias}' == {ExpectedCount}";
}

public sealed record AssertMailboxQueueDepthAssertion(string RelayAlias, int ExpectedDepth) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Mailbox Queue Depth on '{RelayAlias}' == {ExpectedDepth}";
}

public sealed record AssertLogContainsCodeAssertion(string ExpectedCode) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Log Contains: \"{ExpectedCode}\"";
}

// -----------------------------------------------------------------------------
// Phase 6A Scenario Assertions: Disappearing Messages, Reactions & Mutations
// -----------------------------------------------------------------------------

public sealed record AssertMessageExpiredAssertion(string PersonaAlias, string ExpectedText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Message Expired on '{PersonaAlias}': \"{ExpectedText}\"";
}

public sealed record AssertReactionPresentAssertion(string PersonaAlias, string MessageText, string ExpectedEmoji) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Reaction on '{PersonaAlias}': '{ExpectedEmoji}' on \"{MessageText}\"";
}

public sealed record AssertMessageEditedAssertion(string PersonaAlias, string ExpectedNewText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Message Edited on '{PersonaAlias}': \"{ExpectedNewText}\"";
}

public sealed record AssertMessageTombstonedAssertion(string PersonaAlias, string OriginalMessageText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Message Tombstoned on '{PersonaAlias}': \"{OriginalMessageText}\"";
}

public sealed record AssertMessageReadAssertion(string PersonaAlias, string MessageText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Message Marked Read on '{PersonaAlias}': \"{MessageText}\"";
}

// -----------------------------------------------------------------------------
// Phase 6B Scenario Assertions: Profile & Identicon Sync
// -----------------------------------------------------------------------------

public sealed record AssertProfileSyncedAssertion(string ObserverAlias, string TargetAlias, string ExpectedDisplayName) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Profile Synced on '{ObserverAlias}' for '{TargetAlias}' == \"{ExpectedDisplayName}\"";
}

// -----------------------------------------------------------------------------
// Phase 7 Scenario Assertions: Group Epochs, Message Delivery & Forward Secrecy
// -----------------------------------------------------------------------------

public sealed record AssertGroupEpochAssertion(string GroupName, ulong ExpectedEpoch) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Group Epoch for '{GroupName}' == {ExpectedEpoch}";
}

public sealed record AssertGroupMessageDeliveredAssertion(string PersonaAlias, string GroupName, string ExpectedText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Group Message Delivered: '{PersonaAlias}' in '{GroupName}' received \"{ExpectedText}\"";
}

public sealed record AssertMemberCannotDecryptAssertion(string PersonaAlias, string GroupName, string ExpectedText) : ScenarioAssertionDefinition
{
    public override string DisplayText => $"Assert Forward Secrecy: '{PersonaAlias}' CANNOT decrypt \"{ExpectedText}\" in '{GroupName}'";
}
