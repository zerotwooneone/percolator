using System;
using System.Collections.Generic;
using System.Linq;
using ObservableCollections;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Scenarios;

internal sealed class FlightRecorderStateService : IFlightRecorderStateService
{
    private readonly ReactiveProperty<bool> _isRecording = new(false);
    private readonly ObservableList<ScenarioStepDefinition> _recordedSteps = new();
    private readonly ReactiveProperty<string?> _activeReplayStatus = new(null);

    public ReadOnlyReactiveProperty<bool> IsRecording => _isRecording;
    public IReadOnlyObservableList<ScenarioStepDefinition> RecordedSteps => _recordedSteps;
    public ReadOnlyReactiveProperty<string?> ActiveReplayStatus => _activeReplayStatus;

    public void StartRecording() => _isRecording.Value = true;
    public void StopRecording() => _isRecording.Value = false;
    public void ToggleRecording() => _isRecording.Value = !_isRecording.Value;

    public void ClearSteps()
    {
        UiTaskRunner.PostToUi(() => _recordedSteps.Clear());
    }

    public void RecordStep(ScenarioStepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!_isRecording.Value) return;

        UiTaskRunner.PostToUi(() => _recordedSteps.Add(step));
    }

    public void SetReplayStatus(string? status)
    {
        UiTaskRunner.PostToUi(() => _activeReplayStatus.Value = status);
    }

    public ScenarioDocument BuildScenario(string name, string? description = null)
    {
        var steps = _recordedSteps.ToList();
        var assertions = new List<ScenarioAssertionDefinition>();

        // Synthesize assertions from recorded actions
        foreach (var step in steps)
        {
            if (step is ApprovePendingHandshakeStep approve)
            {
                if (!assertions.Any(a => a is AssertDirectRatchetSessionAssertion r &&
                                         ((r.Alias1 == approve.OwnerAlias && r.Alias2 == approve.SenderAlias) ||
                                          (r.Alias1 == approve.SenderAlias && r.Alias2 == approve.OwnerAlias))))
                {
                    assertions.Add(new AssertDirectRatchetSessionAssertion(approve.OwnerAlias, approve.SenderAlias));
                }
            }
            else if (step is SendDirectMessageStep send)
            {
                assertions.Add(new AssertMessageDeliveredAssertion(send.RecipientAlias, send.Text));
            }
        }

        assertions.Add(new AssertZeroErrorsAssertion());

        return new ScenarioDocument
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Recorded Scenario" : name,
            Description = description ?? $"Recorded interactively on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            Steps = steps,
            Assertions = assertions
        };
    }

    public void Dispose()
    {
        _isRecording.Dispose();
        _activeReplayStatus.Dispose();
    }
}
