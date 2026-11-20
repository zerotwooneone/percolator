<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->

# UI State Service Specification (`UiStateService.spec.md`)

This specification governs all stateful UI presentation services (`*StateService.cs`) and shared presentation coordinators across `Percolator.Desktop`.

Any file governed by this specification MUST include this header comment on line 1:
```csharp
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.
```

---

## 1. Quick Rules & Invariants

| ID | Category | MUST DO (Mandatory) | MUST NOT DO (Strictly Prohibited) |
| :-: | :--- | :--- | :--- |
| **[UISS-001]** | **Multi-ViewModel Justification** | Introduce a stateful UI service **only** when state, cached data, or reactive events are legitimately shared across two or more distinct ViewModels. | MUST NOT create stateful UI services for state consumed by or scoped to a single ViewModel. Single-view state MUST reside directly in that ViewModel. |
| **[UISS-002]** | **Zero UI Thread Affinity & Dispatcher Prohibition** | Keep services 100% free-threaded and thread-agnostic. Expose observables that run on caller/background execution contexts without UI thread marshaling. | MUST NOT import `Avalonia.Threading`, invoke `Dispatcher.UIThread`, or reference Avalonia UI controls/visuals inside a UI state service. |
| **[UISS-003]** | **Thread-Safe Encapsulated Mutations** | Synchronize all reads and writes to internal mutable state using explicit lock synchronization primitives (`lock (_lock)`). | MUST NOT expose mutable collections or fields directly to external callers, allowing race conditions or bypassing service invariants. |
| **[UISS-004]** | **Out-of-Lock Event Publication** | Emit reactive notifications (`Subject.OnNext`, `Observable.Emit`) strictly **outside** internal synchronization locks to prevent lock-inversion deadlocks with observers. | MUST NOT invoke `OnNext()` or fire external callbacks while holding internal state locks. |
| **[UISS-005]** | **Read-Only Public Projections** | Expose public collections as read-only synchronized views (`ISynchronizedView<TSource, TView>`) or read-only collections (`IReadOnlyList<T>`). All mutations MUST happen via explicit semantic methods on the service. | MUST NOT expose raw mutable `ObservableList<T>`, `List<T>`, or `IList<T>` properties that allow external callers to mutate collection state directly. |
| **[UISS-006]** | **Ephemeral Events vs. Continuous State** | Use `Subject<T>` (exposed as `Observable<T>`) for one-shot transient events (`ConnectionSucceeded`). Use `ReadOnlyReactiveProperty<T>`, `BehaviorSubject<T>`, or property getters backed by an `Observable<T>` change feed for continuous state (`CurrentPersona`, `ActiveId`) so late subscribers receive the latest value. | MUST NOT expose `BindableReactiveProperty<T>` from services (`BindableReactiveProperty` belongs exclusively to ViewModels for XAML data-binding). MUST NOT use unbuffered `Subject<T>` for persistent state snapshots where late subscribers miss the current value. |
| **[UISS-007]** | **Multi-Persona & Session Teardown Isolation** | Clear, scope, or reset cached in-memory state when the active persona changes or when the vault session locks (`SYS-SESSION-LOCK-ZEROIZATION`). | MUST NOT leak volatile state, drafts, or connection models across persona boundaries or keep sensitive UI state in memory after session lock. |
| **[UISS-008]** | **Non-Blocking Background Delegation** | Delegate heavy compute, IO, socket orchestration, or network retries to injected background workers or application workflows. | MUST NOT perform synchronous blocking IO, execute long-running loops, or invoke `.Result` / `.Wait()` inside UI state services. |
| **[UISS-009]** | **Deterministic Disposal & Subject Completion** | Implement `IDisposable`. Teardown MUST dispose subscriptions via `DisposableBag`, call `OnCompleted()` on all owned subjects, and dispose subjects safely. | MUST NOT leave background subscriptions unanchored or dispose subjects without completing them, stranding downstream reactive pipelines. |
| **[UISS-010]** | **Pure In-Memory Testability** | Author 100% of state service unit tests using standard in-memory test runners (`[Test]` / `[Fact]`) with concurrency, fake timers, and domain primitives. | MUST NOT require headless UI test runners (`[AvaloniaTest]`) or Avalonia platform initialization to test UI state services. |

---

## 2. Architectural Boundaries & Justification

### 2.1 When to Use a UI State Service ([UISS-001])

Stateful UI services exist to solve a specific problem in responsive desktop applications: **cross-ViewModel state coordination without tight coupling**.

```
                           ┌──────────────────────────────┐
                           │    CurrentPersonaService /   │
                           │  PeerConnectionStateService  │
                           └──────────────┬───────────────┘
                                          │
                  ┌───────────────────────┼───────────────────────┐
                  ▼                       ▼                       ▼
      ┌───────────────────────┐ ┌───────────────────┐ ┌───────────────────────┐
      │  MainWindowViewModel  │ │ChatsSidebarView-  │ │     ChatViewModel     │
      │ (Pending Badge Light) │ │ Model (DM List)   │ │(Offline Pending Banner│
      └───────────────────────┘ └───────────────────┘ └───────────────────────┘
```

* **Valid Scenario**: Staged outbound peer connections are displayed in the Left Sidebar (badge count), the Right Sidebar (conversation item with *"Pending..."* badge), and the Content Area (`ChatView` shows *"Waiting for peer to come online"* banner and disables composer). `PeerConnectionStateService` owns this shared transient presentation state.
* **Invalid Scenario**: A search filter text box and search results list in a single dialog window. This state belongs strictly inside the dialog's ViewModel. Introducing a state service for single-view state violates **[UISS-001]** and **[META-014]** (YAGNI).

### 2.2 Threading & Dispatcher Rules ([UISS-002])

UI State Services are shared singletons or session-scoped services. They receive signals from multiple sources:
- Background transport retry workers (Thread Pool)
- Domain event handlers (Network IO threads)
- User commands invoked from ViewModels (UI Thread)

```csharp
// ❌ BAD: Service assumes UI thread or dispatches to Avalonia UI thread
using Avalonia.Threading; // VIOLATION: UI framework dependency in state service!

public void AddItem(PendingItem item)
{
    Dispatcher.UIThread.Post(() => _items.Add(item)); // VIOLATION: [UISS-002]
}

// ✅ GOOD: Service is completely thread-agnostic with explicit lock synchronization
public void AddItem(PendingItem item)
{
    ArgumentNullException.ThrowIfNull(item);
    lock (_gate)
    {
        _items.Add(item);
    }
}
```

Consuming ViewModels are **solely responsible** for observing on or marshaling to the UI thread:
```csharp
// ✅ GOOD: ViewModel marshals reactive events to UI thread before updating properties
stateService.ConnectionSucceeded
    .ObserveOnUI() // or .ObserveOn(SynchronizationContext.Current)
    .Subscribe(model => OnConnectionSucceeded(model))
    .AddTo(ref Bag);
```

---

## 3. High-Value Patterns: Good vs. Bad

### 3.1 Lock Order & Event Publication Outside Locks ([UISS-004])

Publishing reactive notifications (`Subject.OnNext`) inside an internal synchronization lock can cause catastrophic **lock-inversion deadlocks** when an observer synchronously queries the service or acquires an external lock:

```csharp
// ❌ BAD: Publishing OnNext while holding internal lock
public void CompleteConnection(OutboundPendingConnectionModel model)
{
    lock (_lock)
    {
        _pendingConnections.Remove(model);
        // DEADLOCK RISK: If an observer synchronously calls IsPending(id) on another thread!
        _connectionSucceeded.OnNext(model); 
    }
}

// ✅ GOOD: State mutated inside lock; notification published OUTSIDE lock
public void CompleteConnection(OutboundPendingConnectionModel model)
{
    bool removed;
    lock (_lock)
    {
        removed = _pendingConnections.Remove(model);
    }

    if (removed)
    {
        _connectionSucceeded.OnNext(model);
    }
}
```

### 3.2 Read-Only Projections vs. Mutable Collection Leaks ([UISS-005])

```csharp
// ❌ BAD: Exposing mutable ObservableList allows external callers to bypass lock & invariants
public sealed class PeerConnectionStateService : IPeerConnectionStateService
{
    private readonly ObservableList<OutboundPendingConnectionModel> _list = new();

    // VIOLATION: External callers can call .Clear() or .Add() without acquiring _lock!
    public ObservableList<OutboundPendingConnectionModel> PendingOutboundConnections => _list;
}

// ✅ GOOD: Service exposes read-only synchronized view or immutable projection
public sealed class PeerConnectionStateService : IPeerConnectionStateService
{
    private readonly ObservableList<OutboundPendingConnectionModel> _list = new();

    // External callers observe mutations but cannot mutate directly
    public ISynchronizedView<OutboundPendingConnectionModel, OutboundPendingConnectionModel> CreateView()
    {
        lock (_lock)
        {
            return _list.CreateView(x => x);
        }
    }

    // Mutations are strictly encapsulated behind semantic service methods
    public void StagePendingOutbound(OutboundPendingConnectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_lock)
        {
            if (_list.Any(p => p.PeerId == model.PeerId)) return;
            _list.Add(model);
        }
        _retryWorker.EnqueueRetry(model);
    }
}
```

### 3.3 Ephemeral Events vs. Continuous State ([UISS-006])

```csharp
// ❌ BAD: Service exposes BindableReactiveProperty (UI binding construct in a service) or unbuffered Subject
public sealed class CurrentPersonaService : ICurrentPersonaService
{
    // VIOLATION: BindableReactiveProperty belongs exclusively in ViewModels for XAML binding!
    public BindableReactiveProperty<ActivePersonaModel?> CurrentPersona { get; }

    // VIOLATION: Late-subscribing ViewModels miss the current persona with unbuffered Subject!
    private readonly Subject<ActivePersonaModel?> _changed = new();
    public Observable<ActivePersonaModel?> CurrentPersonaChanged => _changed;
}

// ✅ GOOD: Service exposes ReadOnlyReactiveProperty<T> or snapshot getter + change feed Observable<T>
public sealed class CurrentPersonaService : ICurrentPersonaService
{
    private readonly ReactiveProperty<ActivePersonaModel?> _currentPersona = new();

    // Free-threaded, UI-agnostic reactive property from core R3
    public ReadOnlyReactiveProperty<ActivePersonaModel?> CurrentPersona => _currentPersona;
}

// ✅ ViewModel converts the service's reactive property to BindableReactiveProperty for Avalonia XAML:
public sealed class ShellViewModel : ViewModelBase
{
    public BindableReactiveProperty<ActivePersonaModel?> ActivePersona { get; }

    public ShellViewModel(ICurrentPersonaService personaService)
    {
        ActivePersona = personaService.CurrentPersona
            .ObserveOnUI()
            .ToBindableReactiveProperty()
            .AddTo(ref Bag);
    }
}
```

### 3.4 Teardown & Subject Disposal Protocol ([UISS-009])

```csharp
// ❌ BAD: Disposing subjects abruptly without OnCompleted()
public void Dispose()
{
    _bag.Dispose();
    _subject.Dispose(); // Downstream observers may fail or terminate unexpectedly
}

// ✅ GOOD: Complete then dispose all owned reactive subjects
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    _bag.Dispose();

    _connectionSucceeded.OnCompleted();
    _connectionSucceeded.Dispose();

    _connectionCancelled.OnCompleted();
    _connectionCancelled.Dispose();
}
```

---

## 4. Common AI Traps & Anti-Patterns

| Trap | Root Cause | Preventive Rule |
| :--- | :--- | :--- |
| **Injecting Dispatcher in Service** | AI assumes UI events must be marshaled inside the service before firing `OnNext`. | **[UISS-002]**: Services MUST NOT touch UI threads. ViewModels marshal with `.ObserveOnUI()`. |
| **Single-VM State Service** | AI extracts ViewModel properties into a "service" to reduce ViewModel line count. | **[UISS-001]**: Reject state services that serve only one ViewModel. |
| **Exposing Mutable Collections** | AI returns the backing `ObservableList<T>` directly for convenience. | **[UISS-005]**: Expose `ISynchronizedView` or `IReadOnlyList<T>`. |
| **Lock-Inversion Deadlock** | AI places `_subject.OnNext(...)` inside `lock (_lock)`. | **[UISS-004]**: Always publish reactive events *after* exiting the lock block. |
| **Zombie State on Persona Switch** | AI fails to clear state when switching personas, leaking contacts/drafts. | **[UISS-007]**: Bind to `CurrentPersonaChanged` or provide explicit `Reset()`. |

---

## 5. Citations & Verified Sources

* **R3 Reactive Extension Standards**: [`Percolator.Desktop/spec/R3.spec.md`](./R3.spec.md) ([R3-001]–[R3-014])
* **ObservableCollections Framework**: [Cysharp/ObservableCollections](https://github.com/Cysharp/ObservableCollections)
* **QMS Invariants**: `SYS-SESSION-LOCK-ZEROIZATION`, `SYS-CONTACT-CONSENT-GATE`, `SYS-PRE-ACTIVATION-STEALTH`
