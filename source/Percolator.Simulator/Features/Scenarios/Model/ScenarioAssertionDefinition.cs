using System.Text.Json.Serialization;

namespace Percolator.Simulator.Features.Scenarios.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AssertDirectRatchetSessionAssertion), "AssertDirectRatchetSession")]
[JsonDerivedType(typeof(AssertMessageDeliveredAssertion), "AssertMessageDelivered")]
[JsonDerivedType(typeof(AssertZeroErrorsAssertion), "AssertZeroErrors")]
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
