using System;
using ObservableCollections;
using Percolator.Simulator.Features.Scenarios.Model;
using R3;

namespace Percolator.Simulator.Features.Scenarios;

internal interface IFlightRecorderStateService : IDisposable
{
    ReadOnlyReactiveProperty<bool> IsRecording { get; }
    IReadOnlyObservableList<ScenarioStepDefinition> RecordedSteps { get; }
    ReadOnlyReactiveProperty<string?> ActiveReplayStatus { get; }

    void StartRecording();
    void StopRecording();
    void ToggleRecording();
    void ClearSteps();
    void RecordStep(ScenarioStepDefinition step);
    void SetReplayStatus(string? status);
    ScenarioDocument BuildScenario(string name, string? description = null);
}
