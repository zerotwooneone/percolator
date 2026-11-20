using System.Collections.Generic;

namespace Percolator.Simulator.Features.Scenarios.Model;

public sealed record ScenarioDocument
{
    public string Name { get; init; } = "Untitled Scenario";
    public string? Description { get; init; }
    public List<ScenarioStepDefinition> Steps { get; init; } = new();
    public List<ScenarioAssertionDefinition> Assertions { get; init; } = new();
}
