using System;
using System.Threading.Tasks;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedPeerItemViewModel : IDisposable
{
    private readonly ISimulatedPeerDirectory _directory;
    private readonly SimulatedPeerModel _model;
    private DisposableBag _bag;

    public SimulatedPeerItemViewModel(ISimulatedPeerDirectory directory, SimulatedPeerModel model)
    {
        _directory = directory;
        _model = model;

        DisplayText = _model.DisplayName
            .Select(name => string.IsNullOrWhiteSpace(name) ? _model.PeerId.ToString()[..8] : name!)
            .ToBindableReactiveProperty(_model.PeerId.ToString()[..8])
            .AddTo(ref _bag);

        RuntimeStateText = _model.RuntimeState
            .Select(state => state.PendingCorrelationId is null
                ? state.UiState.ToString()
                : $"{state.UiState} ({state.PendingCorrelationId.Value.ToString()[..8]})")
            .ToBindableReactiveProperty(_model.RuntimeState.CurrentValue.UiState.ToString())
            .AddTo(ref _bag);

        var toggleOnline = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleOnline.AsObservable()
            .Subscribe(_ => _model.SetOnline(!_model.IsOnline.CurrentValue))
            .AddTo(ref _bag);
        ToggleOnlineCommand = toggleOnline.AddTo(ref _bag);

        var toggleRelayCapable = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        toggleRelayCapable.AsObservable()
            .Subscribe(_ => _model.SetRelayCapable(!_model.IsRelayCapable.CurrentValue))
            .AddTo(ref _bag);
        ToggleRelayCapableCommand = toggleRelayCapable.AddTo(ref _bag);

        var remove = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        remove.AsObservable()
            .SubscribeAwait(async (_, ct) => await _directory.RemovePeerAsync(_model.PeerId, ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        RemoveCommand = remove.AddTo(ref _bag);

        MarkOutboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkOutboundPending(Guid.NewGuid()))
            .AddTo(ref _bag);

        MarkInboundPendingCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkInboundPending(Guid.NewGuid()))
            .AddTo(ref _bag);

        MarkEstablishedCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.MarkEstablished())
            .AddTo(ref _bag);

        ClearRuntimeStateCommand = Observable.Return(true)
            .ToReactiveCommand<Unit>(_ => _model.ClearRuntimeState())
            .AddTo(ref _bag);
    }

    public Guid PeerId => _model.PeerId;

    public BindableReactiveProperty<string> DisplayText { get; }

    public BindableReactiveProperty<string> RuntimeStateText { get; }

    public ReactiveCommand<Unit> ToggleOnlineCommand { get; }
    public ReactiveCommand<Unit> ToggleRelayCapableCommand { get; }
    public ReactiveCommand<Unit> RemoveCommand { get; }

    public ReactiveCommand<Unit> MarkOutboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkInboundPendingCommand { get; }
    public ReactiveCommand<Unit> MarkEstablishedCommand { get; }
    public ReactiveCommand<Unit> ClearRuntimeStateCommand { get; }

    public void Dispose()
    {
        _bag.Dispose();
    }
}
