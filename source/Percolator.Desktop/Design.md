# Percolator.Desktop

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Desktop` is the cross-platform Avalonia UI presentation and composition-root deployable. It hosts the two-stage dependency injection lifecycle, enforces the network stealth startup gate, implements the presentation sinks of [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md), and renders reactive MVVM views over the application and infrastructure layers.

- **In Scope:** Bootstrap/Stage-2 container handoff, vault unlock and identity-selection UI gates, UI-agnostic stateful presentation services, transient R3 reactive ViewModels, Avalonia views, and custom P2P drawing controls.
- **Out of Scope:** Domain business rules, cryptographic ratchets, SQL queries, and multi-peer network simulation (segregated into `Percolator.Simulator`).

---

## 2. Architectural Invariants and Encapsulation

1. **Total Network Stealth Until Explicit Identity Activation:** Launching the application and unlocking the database binds **zero** network sockets. Kestrel listeners and outbound transport channels start strictly after the user activates a persona in the identity gate. Locking the session immediately tears down all sockets, clears SQLite connection pools, zeroizes the master key handle (per [`Percolator.Infrastructure`](../Percolator.Infrastructure/Design.md)), disposes the Stage 2 `IHost`, and returns to the unlock screen.
2. **Domain Isolation & Presentation-Owned Reactive Models:** Domain models are never bound directly to XAML views. Reactive presentation models (`*Model`) and immutable `.ToSnapshot()` records live exclusively in `Percolator.Desktop`.
3. **UI-Agnostic Stateful Services vs. Transient ViewModels:** Shared cross-view state is owned by thread-safe stateful services (`ObservableList<T>` / `ObservableDictionary<TKey, TValue>`) that never reference UI dispatchers; ViewModels are transient projections created via `ISynchronizedView` and deterministically disposed on view/item removal (`.ObserveRemove()`).
4. **R3 Reactive Threading Discipline:** High-frequency streams are filtered, debounced, or frame-sampled on background threads using injected `System.TimeProvider` before marshaling to `AvaloniaSynchronizationContext` at the terminal subscription step. Worker threads never enumerate UI-bound `NotifyCollectionChangedSynchronizedViewList` wrappers.

---

## 3. Key Technical Design Choices

1. **Debounced `AwaitOperation.Drop` Reload Coordinators:**
   - *Design Choice:* Bursts of inbound domain events trigger read-model refreshes through an R3 `.Debounce(...).SelectAwait(..., AwaitOperation.Drop)` pipeline so redundant triggers arriving while a database read is already in flight are dropped rather than queued.
   - *Rejected Alternative:* Firing a database query per inbound event or subscribing to a global "state changed" event that clears and repopulates UI collections, causing SQLite lock contention and UI frame stutter.
2. **LRU-Cached Conversation Scopes (`SessionScopeFactory`):**
   - *Design Choice:* Active conversation child DI scopes (`IServiceScope` + `ChatViewModel`) are held in a bounded LRU cache so switching between recent chats preserves message drafts and scroll position while evicting and disposing older scopes cleanly.
   - *Rejected Alternative:* Singleton ViewModels per conversation (unbounded RAM/subscription growth) or recreating ViewModels on every tab switch (destroying drafts and scroll state).
3. **Direct-Canvas Bitfield Rendering (`PieceMapControl`):**
   - Large file transfer bitfields render directly onto an Avalonia `DrawingContext` rather than instantiating thousands of individual XAML visual tree elements.
