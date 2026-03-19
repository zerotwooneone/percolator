using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public enum SimulatorDiagnosticEventType
{
    PeerCreated = 0,
    PeerRemoved = 1,
    PeerOnlineChanged = 2,
    PeerRelayCapableChanged = 3,
    PreKeyPublishRelationshipAdded = 4,
    PreKeyPublishRelationshipRemoved = 5,
    HandshakeStateTransition = 6,
    RelayEnqueued = 7,
    RelayDelivered = 8,
    RelayDropped = 9,
    RelayCorrupted = 10,
    RelayReordered = 11,
    DecryptFailure = 12,
    PreKeyBundleFetched = 13,
    StandardHandshakeHelloEnqueued = 14,
    RelayRoutingFailure = 15
}

public sealed record SimulatorDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    SimulatorDiagnosticEventType EventType,
    string Message,
    Guid? PeerId = null,
    Guid? RelayHostPeerId = null,
    Guid? AckId = null,
    string? ContextTag = null);

public interface ISimulatorDiagnosticsService
{
    ObservableCollection<SimulatorDiagnosticEvent> Events { get; }

    void Emit(SimulatorDiagnosticEventType eventType, string message, Guid? peerId = null, Guid? relayHostPeerId = null, Guid? ackId = null, string? contextTag = null);

    void Clear();

    IReadOnlyList<SimulatorDiagnosticEvent> GetRecentEvents(int max);
}

public sealed class SimulatorDiagnosticsService : ISimulatorDiagnosticsService
{
    private const int MaxEvents = 2000;

    private readonly ObservableCollection<SimulatorDiagnosticEvent> _events = new();
    private readonly object _gate = new();

    public ObservableCollection<SimulatorDiagnosticEvent> Events { get; }

    public SimulatorDiagnosticsService()
    {
        Events = _events;
    }

    public void Emit(
        SimulatorDiagnosticEventType eventType,
        string message,
        Guid? peerId = null,
        Guid? relayHostPeerId = null,
        Guid? ackId = null,
        string? contextTag = null)
    {
        var ev = new SimulatorDiagnosticEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: eventType,
            Message: message,
            PeerId: peerId,
            RelayHostPeerId: relayHostPeerId,
            AckId: ackId,
            ContextTag: contextTag);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            lock (_gate)
            {
                _events.Add(ev);
                while (_events.Count > MaxEvents)
                {
                    _events.RemoveAt(0);
                }
            }
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                _events.Add(ev);
                while (_events.Count > MaxEvents)
                {
                    _events.RemoveAt(0);
                }
            }
        });
    }

    public void Clear()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(Clear);
            return;
        }

        lock (_gate)
        {
            _events.Clear();
        }
    }

    public IReadOnlyList<SimulatorDiagnosticEvent> GetRecentEvents(int max)
    {
        if (max <= 0) return Array.Empty<SimulatorDiagnosticEvent>();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            lock (_gate)
            {
                return _events.TakeLast(max).ToList();
            }
        }

        return dispatcher.Invoke(() =>
        {
            lock (_gate)
            {
                return (IReadOnlyList<SimulatorDiagnosticEvent>)_events.TakeLast(max).ToList();
            }
        });
    }
}

public sealed class SimulatorDiagnosticsTabViewModel : IDisposable
{
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatorStateService _state;

    private readonly Subject<Unit> _rebuildRequests = new();

    private readonly ObservableCollection<SimulatorDiagnosticEvent> _filtered = new();
    private DisposableBag _bag;

    private NotifyCollectionChangedEventHandler? _diagnosticsChangedHandler;
    private NotifyCollectionChangedEventHandler? _peersChangedHandler;

    public SimulatorDiagnosticsTabViewModel(ISimulatorDiagnosticsService diagnostics, ISimulatorStateService state)
    {
        _diagnostics = diagnostics;
        _state = state;

        Events = new ReadOnlyObservableCollection<SimulatorDiagnosticEvent>(_filtered);

        SelectedPeerId = new BindableReactiveProperty<Guid?>(null).AddTo(ref _bag);
        SelectedRelayHostPeerId = new BindableReactiveProperty<Guid?>(null).AddTo(ref _bag);
        SelectedEventType = new BindableReactiveProperty<SimulatorDiagnosticEventType?>(null).AddTo(ref _bag);

        PeerFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<Guid?>>>(Array.Empty<SimulatorFilterOption<Guid?>>()).AddTo(ref _bag);
        RelayFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<Guid?>>>(Array.Empty<SimulatorFilterOption<Guid?>>()).AddTo(ref _bag);
        EventTypeFilterOptions = new BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<SimulatorDiagnosticEventType?>>>(Array.Empty<SimulatorFilterOption<SimulatorDiagnosticEventType?>>()).AddTo(ref _bag);

        var clear = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        clear.AsObservable().Subscribe(_ => Clear()).AddTo(ref _bag);
        ClearCommand = clear.AddTo(ref _bag);

        _rebuildRequests
            .Debounce(TimeSpan.FromMilliseconds(100), TimeProvider.System)
            .Subscribe(_ => RebuildFiltered())
            .AddTo(ref _bag);

        _diagnosticsChangedHandler = (_, __) => _rebuildRequests.OnNext(Unit.Default);
        ((INotifyCollectionChanged)_diagnostics.Events).CollectionChanged += _diagnosticsChangedHandler;

        SelectedPeerId.Skip(1).Subscribe(_ => _rebuildRequests.OnNext(Unit.Default)).AddTo(ref _bag);
        SelectedRelayHostPeerId.Skip(1).Subscribe(_ => _rebuildRequests.OnNext(Unit.Default)).AddTo(ref _bag);
        SelectedEventType.Skip(1).Subscribe(_ => _rebuildRequests.OnNext(Unit.Default)).AddTo(ref _bag);

        _peersChangedHandler = (_, __) => RefreshFilterOptions();
        ((INotifyCollectionChanged)_state.Peers).CollectionChanged += _peersChangedHandler;

        RefreshFilterOptions();
        _rebuildRequests.OnNext(Unit.Default);
    }

    public ReadOnlyObservableCollection<SimulatorDiagnosticEvent> Events { get; }

    public BindableReactiveProperty<Guid?> SelectedPeerId { get; }
    public BindableReactiveProperty<Guid?> SelectedRelayHostPeerId { get; }
    public BindableReactiveProperty<SimulatorDiagnosticEventType?> SelectedEventType { get; }

    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<Guid?>>> PeerFilterOptions { get; }
    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<Guid?>>> RelayFilterOptions { get; }
    public BindableReactiveProperty<IReadOnlyList<SimulatorFilterOption<SimulatorDiagnosticEventType?>>> EventTypeFilterOptions { get; }

    public ReactiveCommand<Unit> ClearCommand { get; }

    private void Clear()
    {
        _diagnostics.Clear();
        RefreshEvents();
    }

    private void RefreshEvents()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(RefreshEvents);
            return;
        }

        _filtered.Clear();
    }

    private void RefreshFilterOptions()
    {
        var peers = _state.Peers
            .Select(p => new SimulatorFilterOption<Guid?>(p.PeerId, string.IsNullOrWhiteSpace(p.DisplayName) ? p.PeerId.ToString()[..8] : p.DisplayName!))
            .OrderBy(p => p.Display)
            .ToList();

        peers.Insert(0, new SimulatorFilterOption<Guid?>(null, "All"));
        PeerFilterOptions.Value = peers;

        var relays = _state.Peers
            .Where(p => p.Relay?.IsRelayCapable == true)
            .Select(p => new SimulatorFilterOption<Guid?>(p.PeerId, string.IsNullOrWhiteSpace(p.DisplayName) ? p.PeerId.ToString()[..8] : p.DisplayName!))
            .OrderBy(p => p.Display)
            .ToList();

        relays.Insert(0, new SimulatorFilterOption<Guid?>(null, "All"));
        RelayFilterOptions.Value = relays;

        var types = Enum.GetValues(typeof(SimulatorDiagnosticEventType))
            .Cast<SimulatorDiagnosticEventType>()
            .Select(t => new SimulatorFilterOption<SimulatorDiagnosticEventType?>(t, t.ToString()))
            .OrderBy(t => t.Display)
            .ToList();

        types.Insert(0, new SimulatorFilterOption<SimulatorDiagnosticEventType?>(null, "All"));
        EventTypeFilterOptions.Value = types;
    }

    private void RebuildFiltered()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(RebuildFiltered);
            return;
        }

        var peerId = SelectedPeerId.Value;
        var relayHostId = SelectedRelayHostPeerId.Value;
        var eventType = SelectedEventType.Value;

        var query = _diagnostics.Events.AsEnumerable();
        if (peerId is not null)
        {
            query = query.Where(e => e.PeerId == peerId);
        }
        if (relayHostId is not null)
        {
            query = query.Where(e => e.RelayHostPeerId == relayHostId);
        }
        if (eventType is not null)
        {
            query = query.Where(e => e.EventType == eventType);
        }

        var list = query
            .OrderBy(e => e.TimestampUtc)
            .TakeLast(1000)
            .ToList();

        _filtered.Clear();
        foreach (var ev in list)
        {
            _filtered.Add(ev);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_diagnosticsChangedHandler is not null)
            {
                ((INotifyCollectionChanged)_diagnostics.Events).CollectionChanged -= _diagnosticsChangedHandler;
            }
        }
        catch
        {
        }

        try
        {
            if (_peersChangedHandler is not null)
            {
                ((INotifyCollectionChanged)_state.Peers).CollectionChanged -= _peersChangedHandler;
            }
        }
        catch
        {
        }

        _bag.Dispose();
    }
}

public sealed record SimulatorFilterOption<T>(T Value, string Display);
