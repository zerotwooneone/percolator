using System.Collections.ObjectModel;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorSessionsTabViewModel : IDisposable
{
    private readonly ISimulatorStateService _state;

    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    private readonly ObservableCollection<SimulatorSessionCardViewModel> _cards = new();
    public ReadOnlyObservableCollection<SimulatorSessionCardViewModel> Cards { get; }

    private DisposableBag _bag;

    private CancellationTokenSource? _refreshCts;
    private Task? _refreshLoop;

    public SimulatorSessionsTabViewModel(
        ISimulatorStateService state)
    {
        _state = state;
        Cards = new ReadOnlyObservableCollection<SimulatorSessionCardViewModel>(_cards);

        StartRefreshLoop();
    }

    private void StartRefreshLoop()
    {
        StopRefreshLoop();
        _refreshCts = new CancellationTokenSource();
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    private void StopRefreshLoop()
    {
        try
        {
            _refreshCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var mainPeerId = MainNodeSentinelPeerId;
        var peers = _state.Peers.GetSnapshot();
        var snapshots = _state.SnapshotPeers();

        var cards = new System.Collections.Generic.List<SimulatorSessionCardViewModel>();
        foreach (var p in peers)
        {
            ct.ThrowIfCancellationRequested();
            var store = await _state.TryGetRuntimeStoreAsync(p.PeerId, ct).ConfigureAwait(false);
            if (store is null) continue;

            var session = store.Sessions
                .OrderByDescending(s => s.LastUsedAtUtc)
                .FirstOrDefault(s => s.RemotePeerId == mainPeerId);

            if (session is null) continue;

            var snap = snapshots.FirstOrDefault(x => x.PeerId == p.PeerId);
            cards.Add(CreateCard(p, snap, session));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ResetCards(cards);
            return;
        }

        await dispatcher.InvokeAsync(() => ResetCards(cards)).Task.ConfigureAwait(false);
    }

    private void ResetCards(System.Collections.Generic.IReadOnlyList<SimulatorSessionCardViewModel> cards)
    {
        foreach (var existing in _cards)
        {
            existing.Dispose();
        }
        _cards.Clear();
        foreach (var c in cards)
        {
            _cards.Add(c);
        }
    }

    private SimulatorSessionCardViewModel CreateCard(SimulatedPeerModel peer, SimulatedPeerSnapshot? snapshot, SimulatedSecureSessionDto session)
    {
        var name = !string.IsNullOrWhiteSpace(snapshot?.DisplayName)
            ? snapshot!.DisplayName!
            : (peer.DisplayName.CurrentValue is { } n && !string.IsNullOrWhiteSpace(n) ? n : peer.PeerId.ToString()[..8]);
        var rootHash = TruncateHex(session.RootKey);

        return new SimulatorSessionCardViewModel(
            peerName: name,
            peerIdShort: peer.PeerId.ToString()[..8],
            rootKeyHash: rootHash,
            sendingCounter: session.SendCounter,
            receivingCounter: session.RecvCounter,
            skippedKeysCount: session.SkippedKeysCount);
    }

    private static string TruncateHex(byte[] bytes, int take = 6)
    {
        if (bytes is null || bytes.Length == 0) return "(empty)";
        var hex = Convert.ToHexString(bytes);
        if (hex.Length <= take * 2) return hex;
        return hex[..(take * 2)] + "...";
    }

    public void Dispose()
    {
        StopRefreshLoop();

        foreach (var existing in _cards)
        {
            existing.Dispose();
        }
        _cards.Clear();
        _bag.Dispose();
    }
}

public sealed class SimulatorSessionCardViewModel : IDisposable
{
    private DisposableBag _bag;

    public BindableReactiveProperty<string> PeerName { get; }
    public BindableReactiveProperty<string> PeerIdShort { get; }
    public BindableReactiveProperty<string> RootKeyHash { get; }
    public BindableReactiveProperty<ulong> SendingChainIndex { get; }
    public BindableReactiveProperty<ulong> ReceivingChainIndex { get; }
    public BindableReactiveProperty<int> SkippedKeysCount { get; }

    public SimulatorSessionCardViewModel(
        string peerName,
        string peerIdShort,
        string rootKeyHash,
        ulong sendingCounter,
        ulong receivingCounter,
        int skippedKeysCount)
    {
        PeerName = new BindableReactiveProperty<string>(peerName).AddTo(ref _bag);
        PeerIdShort = new BindableReactiveProperty<string>(peerIdShort).AddTo(ref _bag);
        RootKeyHash = new BindableReactiveProperty<string>(rootKeyHash).AddTo(ref _bag);
        SendingChainIndex = new BindableReactiveProperty<ulong>(sendingCounter).AddTo(ref _bag);
        ReceivingChainIndex = new BindableReactiveProperty<ulong>(receivingCounter).AddTo(ref _bag);
        SkippedKeysCount = new BindableReactiveProperty<int>(skippedKeysCount).AddTo(ref _bag);
    }

    public void Dispose()
    {
        _bag.Dispose();
        Disposable.Dispose(PeerName, PeerIdShort, RootKeyHash, SendingChainIndex, ReceivingChainIndex, SkippedKeysCount);
    }
}
