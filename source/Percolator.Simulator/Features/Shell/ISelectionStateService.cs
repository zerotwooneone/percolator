using System;
using System.Linq;
using ObservableCollections;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal interface ISelectionStateService : IDisposable
{
    ReadOnlyReactiveProperty<SimulatedNodeSummary?> PrimarySelectedNode { get; }
    ReadOnlyReactiveProperty<SimulatedNodeSummary?> SecondarySelectedNode { get; }
    IReadOnlyObservableList<SimulatedNodeSummary> MultiSelectedNodes { get; }

    void SetPrimary(SimulatedNodeSummary? node);
    void SetSecondary(SimulatedNodeSummary? node);
    void ToggleMultiSelect(SimulatedNodeSummary node);
    void ClearMultiSelection();
    void SwapRoles();
}

internal sealed class SelectionStateService : ISelectionStateService
{
    private readonly ReactiveProperty<SimulatedNodeSummary?> _primary = new(null);
    private readonly ReactiveProperty<SimulatedNodeSummary?> _secondary = new(null);
    private readonly ObservableList<SimulatedNodeSummary> _multiSelected = new();

    public ReadOnlyReactiveProperty<SimulatedNodeSummary?> PrimarySelectedNode => _primary;
    public ReadOnlyReactiveProperty<SimulatedNodeSummary?> SecondarySelectedNode => _secondary;
    public IReadOnlyObservableList<SimulatedNodeSummary> MultiSelectedNodes => _multiSelected;

    public void SetPrimary(SimulatedNodeSummary? node)
    {
        _primary.Value = node;
    }

    public void SetSecondary(SimulatedNodeSummary? node)
    {
        _secondary.Value = node;
    }

    public void ToggleMultiSelect(SimulatedNodeSummary node)
    {
        var existing = _multiSelected.FirstOrDefault(x => x.Id == node.Id);
        if (existing is not null)
        {
            _multiSelected.Remove(existing);
        }
        else
        {
            _multiSelected.Add(node);
        }
    }

    public void ClearMultiSelection()
    {
        _multiSelected.Clear();
    }

    public void SwapRoles()
    {
        var temp = _primary.Value;
        _primary.Value = _secondary.Value;
        _secondary.Value = temp;
    }

    public void Dispose()
    {
        _primary.Dispose();
        _secondary.Dispose();
        _multiSelected.Clear();
    }
}
