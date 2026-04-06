using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorHostViewModel : IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly ISimulatorStateInitializer _initializer;
    private readonly ISimulatorRelayAutoDeliverService _relayAutoDeliver;
    private readonly HandshakeSimulatorViewModel _inner;
    private readonly CancellationTokenSource _disposeCts = new();
    private DisposableBag _bag;

    public HandshakeSimulatorHostViewModel(
        IUiDispatcher ui,
        ISimulatorStateInitializer initializer,
        ISimulatorRelayAutoDeliverService relayAutoDeliver,
        HandshakeSimulatorViewModel inner)
    {
        _ui = ui;
        _initializer = initializer;
        _relayAutoDeliver = relayAutoDeliver;
        _inner = inner;

        IsLoading = new BindableReactiveProperty<bool>(true).AddTo(ref _bag);
        Status = new BindableReactiveProperty<string?>("Loading…").AddTo(ref _bag);
        Content = new BindableReactiveProperty<object?>(null).AddTo(ref _bag);

        _ = StartAsync();
    }

    public BindableReactiveProperty<bool> IsLoading { get; }

    public BindableReactiveProperty<string?> Status { get; }

    public BindableReactiveProperty<object?> Content { get; }

    private async Task StartAsync()
    {
        try
        {
            await _initializer.InitializeAsync(_disposeCts.Token).ConfigureAwait(false);

            await _ui.InvokeAsync(() =>
            {
                IsLoading.Value = false;
                Status.Value = null;
                Content.Value = _inner;
            }, _disposeCts.Token).ConfigureAwait(false);

            _relayAutoDeliver.Start();
        }
        catch (Exception ex)
        {
            try
            {
                await _ui.InvokeAsync(() =>
                {
                    IsLoading.Value = true;
                    Status.Value = $"Error: {ex.Message}";
                    Content.Value = null;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        try { _relayAutoDeliver.Stop(); } catch { }
        try { _disposeCts.Cancel(); } catch { }
        try { _disposeCts.Dispose(); } catch { }
        (_inner as IDisposable)?.Dispose();
        _bag.Dispose();
    }
}
