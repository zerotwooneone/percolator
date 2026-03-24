# WPF MVVM + TDD Guidelines (Concise)

- **ViewModel-first composition**
    - Navigate with ViewModels only; Views are created via DataTemplates in Shared/Theme/ViewMappings.xaml.
    - No View instantiation or logic in ViewModels.

- **DI and configuration**
    - App.xaml.cs uses Host.CreateDefaultBuilder() to load appsettings (+Environment) and environment variables.
    - Register options via services.Configure<...>(configuration.GetSection("...")).
    - Add infrastructure with services.AddInfrastructureServices(configuration).
    - Lifetimes: Singleton (Shell/global models), Scoped (identity/session), Transient (helpers).

- **State and reactivity**
    - Use **shared models** (non-ViewModel) as the sharable, in-memory source of truth.
        - Shared models may be reactive (R3) but should **not** be WPF-bindable.
        - Default: shared models use `ReactiveProperty<T>` / `ReadOnlyReactiveProperty<T>`.
        - Do not put `BindableReactiveProperty<T>` in shared models unless explicitly opting into “presentation models” that are WPF-coupled.
    - Use **ViewModels** as the bindable projection layer.
        - ViewModels expose `BindableReactiveProperty<T>` / `ReadOnlyBindableReactiveProperty<T>` for XAML binding via `.Value`.
        - ViewModels own formatting and derived values (e.g., timestamp strings, initials, 99+ unread display).
    - Avoid sharing ViewModels when possible.
        - Prefer sharing models via a singleton store/service (e.g., `SecureChannelsStore`) and let each VM project what it needs.
    - UI thread rules:
        - Only mutate WPF-bound state (collections and bindable properties) on the UI thread.
        - Use `ObserveOnCurrentSynchronizationContext()` and/or marshal via Dispatcher inside stores/services.
    - Dispose reactive state in `DisposeCore()`.

- **UI dispatcher (UI-thread marshalling)**
    - ViewModels should not call `Application.Current.Dispatcher` directly.
    - Inject an `IUiDispatcher` abstraction and marshal UI-bound work through it.
    - This keeps ViewModels testable and avoids hidden global dependencies.

- **Reactive collections (WPF binding)**
    - Services expose reactive collections as `IReadOnlyObservableList<T>` / `IReadOnlyObservableDictionary<TKey, TValue>`.
    - ViewModels project collections using `CreateView(transform)` to produce an `ISynchronizedView<TModel, TViewModel>`.
    - WPF `ItemsControl` bindings should use the adapter returned by `ToNotifyCollectionChanged()`.
        - Call `ToNotifyCollectionChanged()` **once**, store the instance, and dispose it with the ViewModel.
        - Do **not** call `ToNotifyCollectionChanged()` in a property getter.

- **Initialization (explicit, no fire-and-forget in constructors)**
    - Do not start background work from a ViewModel constructor (no `_ = InitializeAsync()` in constructors).
    - Expose an explicit `InitializeAsync(CancellationToken)` method.
    - Call initialization from the composition root / view lifecycle (e.g., the window after `DataContext` is assigned).

- **Commands (R3)**
    - Prefer direct command construction (e.g., `new ReactiveCommand<Unit>()`) over creating commands via dummy observables.
    - Use `SubscribeAwait(..., AwaitOperation.Drop)` to prevent double-execution when a command can be invoked repeatedly.

- **Async and cancellation**
    - Async methods accept CancellationToken; use try/finally for IsLoading flags; avoid blocking UI.

- **TDD essentials**
    - Behavior-first tests with MockBehavior.Strict.
    - Deterministic time via IClock (TestClock in tests).
    - No Views in tests—assert ViewModel state and navigation targets.

- **Commands and validation**
    - Wrap UI intent in ICommand (guard with CanExecute) or async methods via behaviors.
    - Validate in ViewModels/services; expose bindable error state; log unexpected exceptions.

- **Navigation and scoped composition**
    - Shell orchestrates startup: resolve/create self identity, create identity DI scope, set ActiveIdentityContext.
    - Resolve identity-scoped VMs/services from the scope; dispose the scope on identity switch or exit.

- **Styling and theming**
    - Base styles live in Shared/Theme/styles.xaml as global styles (e.g., a base Button style). Component styles derive via BasedOn.
    - Colors use Angular Material–inspired tokens: Primary, Secondary, Tertiary, Surface, Background, Error and their “On”/Container variants (e.g., OnPrimary, PrimaryContainer). Avoid arbitrary names.

- **Angular Material–inspired components**
    - Build WPF components (buttons, text fields, lists, chips, banners/snackbars, cards) that mirror Material semantics: states (hover/pressed/disabled), elevation, and density.
    - Use base styles + Material color tokens; keep behaviors testable and VM-driven.

- **Behaviors and attached properties**
    - For view-only concerns (focus, drag-drop, animations). Keep them dumb; forward to commands/events.

- **Dialogs (VM-first)**
    - Abstract via IDialogService; map DialogViewModel -> DialogView via DataTemplates; mock in tests.

- **Design-time, accessibility, localization**
    - Provide minimal design-time data; don’t run app logic at design-time.
    - Use AutomationProperties and resource-based typography; respect system scaling.
    - Localize user strings; keep culture-invariant logic in ViewModels.

- **UI testing strategy**
    - Prefer ViewModel unit tests; keep UI automation to smoke tests (startup, navigation, simple interaction).