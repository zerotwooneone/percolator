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
    - Use R3 BindableReactiveProperty/ReadOnlyBindableReactiveProperty and ObserveOnCurrentSynchronizationContext() for UI.
    - Dispose reactive state in DisposeCore().

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