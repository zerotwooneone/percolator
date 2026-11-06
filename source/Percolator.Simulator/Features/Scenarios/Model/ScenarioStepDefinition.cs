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
