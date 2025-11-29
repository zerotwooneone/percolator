# Re-architecture Plan: VM-first Composition, TDD

## Goals
- MainWindow hosts a single ContentControl bound to `ShellViewModel.CurrentView`.
- Startup shows a Loading screen (VM+View via DataTemplate).
- After self-identity resolution, show a SessionShell screen (VM+View) which composes:
  - Left: Sessions sidebar (SessionsSidebarViewModel → SessionsSidebarView)
  - Right: Main chat host ContentControl that displays `ChatViewModel` for the selected session.
- Pure VM-first: Views are created by WPF via DataTemplates. No View creation in ViewModels.

## High-level Design
- `ShellViewModel` (Singleton):
  - `CurrentView` (object) and `IsLoading` (bool) for diagnostics only.
  - On startup: set `CurrentView = LoadingViewModel`.
  - Resolve self-identity. If none, set a NewUser VM (out of scope for now). If exists:
    - Create identity DI scope.
    - Resolve `SessionShellViewModel` from that scope and set `CurrentView = sessionShellVm`.
- `SessionShellViewModel` (Identity-scoped):
  - `Sidebar` (SessionsSidebarViewModel).
  - `CurrentContent` (object?): holds the current main area VM (e.g., `ChatViewModel`).
  - Acts as an `ISessionConductor` to respond to session selection: `Show(ChatViewModel)`.
- `SessionsSidebarViewModel` (Identity-scoped):
  - On `SelectedSessionId` change, resolves `ChatViewModel` via `ISessionScopeFactory` and calls conductor to display it.
- `ISessionScopeFactory` (Singleton):
  - Manages per-session scopes.
  - Returns `ChatViewModel` configured for the session.

## DataTemplates (ViewMappings.xaml)
- `LoadingViewModel -> LoadingView`
- `SessionShellViewModel -> SessionShellView`
- `SessionsSidebarViewModel -> SessionsSidebarView`
- `ChatViewModel -> ChatView`

## TDD Plan (Large Chunks)

### Chunk 1 — RED: MainWindow single content host
- Add a test: MainWindow has a `ContentControl` bound to `ShellViewModel.CurrentView`.
- Assert that on startup, `CurrentView` is `LoadingViewModel`.

### Chunk 2 — GREEN: Loading screen implementation
- Add `LoadingViewModel` (POCO) and `LoadingView` (parameterless UserControl).
- Add DataTemplate for `LoadingViewModel`.
- Update `ShellViewModel` to set `CurrentView = new LoadingViewModel()` immediately on startup.
- Remove prior loading overlay in MainWindow (if any), rely on LoadingView.

### Chunk 3 — RED: SessionShell composition after identity
- Add a test: after identity resolves, Shell sets `CurrentView` to a `SessionShellViewModel` (resolved from an identity-scoped provider).
- Verify Shell uses `IServiceScopeFactory.CreateScope()`.

### Chunk 4 — GREEN: Implement SessionShell VM+View
- `SessionShellViewModel` with properties:
  - `Sidebar` (SessionsSidebarViewModel)
  - `CurrentContent` (object?)
  - `Show(object vm)` to set `CurrentContent` (implements `ISessionConductor`).
- `SessionShellView` XAML: two-column grid.
  - Left `ContentControl Content="{Binding Sidebar}"`
  - Right `ContentControl Content="{Binding CurrentContent}"`
- Add DataTemplate mapping for `SessionShellViewModel`.
- Shell, after identity:
  - From identity scope, resolve `SessionShellViewModel` and `SessionsSidebarViewModel`.
  - Set `sessionShell.Sidebar = sessionsSidebarVm`.
  - `CurrentView = sessionShell`.

### Chunk 5 — RED: Session selection drives chat
- Add a test: When `SessionsSidebarViewModel.SelectedSessionId` is set, the `SessionShellViewModel.CurrentContent` becomes a `ChatViewModel`
  - Confirm it was resolved via `ISessionScopeFactory.GetOrCreate(id, header).ViewModel`.

### Chunk 6 — GREEN: Wire selection to SessionShell conductor
- Update `SessionsSidebarViewModel` selection handler:
  - Resolve `resolved = _sessionFactory.GetOrCreate(id, header)`.
  - Call `conductor.Show(resolved.ViewModel)`.
- Inject an `ISessionConductor` into `SessionsSidebarViewModel` (provided by SessionShell VM when building the identity scope).
- Optional: when selection cleared, set `CurrentContent = null` (SessionShell can show a welcome panel in its view).

### Chunk 7 — REFACTOR: Cleanups
- Remove `ISidebarHost` and any MainWindow sidebar.
- MainWindow XAML: single `ContentControl` bound to `CurrentView`.
- Ensure all Views have parameterless constructors.
- DI registrations:
  - `MainWindow` (Singleton)
  - `ShellViewModel` (Singleton)
  - `LoadingViewModel` (Singleton or Transient)
  - `SessionShellViewModel` (Scoped to identity)
  - `SessionsSidebarViewModel` (Scoped to identity)
  - `ISessionScopeFactory` (Singleton)
  - `ChatViewModel` (Scoped per session via SessionScopeFactory)
  - Views are created by DataTemplates (no DI needed for Views)

### Chunk 8 — STABILIZE: Test pass and diagnostics
- Run tests; address any failures.
- Add debug logging where useful:
  - In `ShellViewModel` when swapping `CurrentView`.
  - In `SessionsSidebarViewModel` on selection.
- Inspect WPF binding errors in Output if anything doesn’t render as expected.

## Risks & Mitigations
- DataTemplate mismatches → Ensure correct namespaces and `DataType`.
- Identity scoping → Don’t resolve scoped VMs from singletons directly. Always create identity scope first.
- Per-session scoping → Keep `ISessionScopeFactory` responsible for Chat VM lifetime.

## Rollout Steps Summary
1) Implement Loading VM+View and swap to it at Shell startup.
2) Add SessionShell VM+View and set as `CurrentView` after identity scope creation.
3) Move selection routing to SessionShell via conductor pattern.
4) Clean up DI and old host/overlay code.
5) Stabilize with tests and binding diagnostics.
