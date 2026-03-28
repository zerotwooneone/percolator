# System Context: WPF MVVM & UI Projection

**Core Paradigm:** ViewModels project Domain state to the UI thread. The UI is strictly a reflection of the Domain. Background threads must never touch UI-bound collections.

## 1. ViewModels & State Projection
* **Read-Only Bindings:** Expose `BindableReactiveProperty<T>` as read-only instances. Mutate via `.Value`.
* **WPF Virtualization Trap:** **Never** bind `ItemsSource` to a `BindableReactiveProperty<IReadOnlyList<T>>` that gets replaced. It destroys WPF virtualization and UI state.
    * *Mandate:* Project collections using `source.CreateView(…).AddTo(ref _bag)` and bind to its `.ToNotifyCollectionChanged()` adapter.
* **Sorting Rule:** **Never** sort collections in the ViewModel. Always use `<CollectionViewSource>` in XAML.
* **Dynamic Filtering:** Do not recreate views. Use `view.AttachFilter((model, vm) => ...)` and `view.ResetFilter()`. If an *inner* reactive property changes, manually trigger `view.RefreshFilter(model.Instance)`.

## 2. Dispatcher Bridging & Threading
* **Filter Early, Marshal Late:** When bridging background streams to the UI:
  ```csharp
  model.Property.DistinctUntilChanged() // 1. Filter off-thread
       .Sample(TimeSpan.FromMilliseconds(16)) // 2. Pace (prevent Dispatcher firehose)
       .ObserveOnCurrentSynchronizationContext() // 3. Marshal to WPF
       .ToBindableReactiveProperty()
       .AddTo(ref _bag);
  ```
* **The UI-Bound Enumeration Crash:** `NotifyCollectionChangedSynchronizedViewList<T>` is strictly bound to the UI Dispatcher. **Never** read, iterate, or `.ToList()` this adapter from a background thread (e.g., `Task.Run`). It will throw cross-thread exceptions.
    * *Fix:* If a background loop needs data, query the pure Domain `ObservableList` instead, or marshal via `await _ui.InvokeAsync(() => uiList.ToList())`.

## 3. Memory Leaks & Disposal
* **The `CreateView` Leak:** Removing a domain model from an `ObservableList` removes it from the `ISynchronizedView`, but **does not dispose the child ViewModel**.
    * *Mandate:* You must explicitly subscribe to removals:
      ```csharp
      view.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);
      ```
* **Nested Command Leaks:** Any nested/child ViewModel that creates `ReactiveCommand`s or `BindableReactiveProperty`s must implement `IDisposable` and manually dispose them.
* **ViewModel Disposal:** If a ViewModel owns reactive fields (commands, properties, views, `ToNotifyCollectionChanged` adapters), it must be `IDisposable` and use a `DisposableBag`.