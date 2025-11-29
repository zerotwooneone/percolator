# Desktop.Wpf — Initialization Plan

This plan implements the architecture described in `Desktop.Wpf/readme.md` and the provided UI in `mockup.png`. Work is organized into large chunks that can be implemented end‑to‑end in sequence.

---

## Chunk 01 — Host bootstrap, DI, and project structure

- **Create Generic Host**
  - Build `IHost` in `App.xaml.cs` (`Host.CreateDefaultBuilder`) and wire `ApplicationStarted`/`Stopping` to manage services.
  - Register logging, configuration, and `IServiceProvider` integration for ViewModels.
- **Feature-based folders**
  - `Features/Shell`
  - `Features/Sessions`
  - `Features/Chat`
  - `Shared` (controls, converters, resources, theme)
- **Packages**
  - `R3` for reactive properties.
  - `Microsoft.Extensions.*` (DependencyInjection, Hosting, Logging, Options.ConfigurationExtensions).
- **App resources**
  - Create `Shared/Theme/Colors.xaml`, `Typography.xaml`, `Styles.xaml`, merged in `App.xaml`.
- **Navigation services (simple)**
  - Register `INavigationService` (interface) and a shell-local implementation to swap views in the content region.

Outcome: WPF app runs under `IHost`, has DI, logging, configuration, and a feature-sliced layout ready for UI work.

---

## Chunk 02 — Shell UI matching mockup skeleton

- **Main window layout**
  - Left sidebar (fixed ~320px) + right content region.
  - Title area with app badge and name.
  - Search box at top of sidebar.
  - Sessions list placeholder with avatar circle, name, snippet, timestamp, unread badge.
  - Footer with current user and short ID.
- **Welcome panel (content area)**
  - Centered lock icon, headline, body text, and three chips (AES‑256, NO LOGS, P2P) as static placeholders.
- **Resources**
  - Add icons (lock, search) via `Geometry` or vector resources; keep dark theme defaults from mockup.

Outcome: Visual shell approximates `mockup.png` with placeholder data and styles.

---

## Chunk 03 — Reactive foundation (R3) and MVVM base

- **Base classes**
  - `Shared/Reactive/DisposableBag` helper and `Disposable.Dispose(...)` usage.
  - `Shared/Mvvm/ViewModelBase` exposing `CompositeDisposable` and convenience `ToBindableReactiveProperty` extensions.
- **Commands**
  - Lightweight `AsyncRelayCommand` in `Shared/Mvvm` for button actions.
- **Binding convention**
  - All bindable state uses `BindableReactiveProperty<T>`; internal state uses `ReactiveProperty<T>`.

Outcome: ViewModels can expose `.Value` properties and commands; XAML bindings use `.Value`.

---

## Chunk 04 — Sessions feature (sidebar)

- **Models/Service**
  - `SessionListItem { Id, DisplayName, LastMessagePreview, Timestamp, UnreadCount, AccentColor }`.
  - `ISessionDirectory` with in-memory mock implementation returning ~5 sessions.
- **ViewModel**
  - `SessionsSidebarViewModel` with
    - `SearchText: BindableReactiveProperty<string>`
    - `Items: ReadOnlyObservableCollection<SessionListItem>` produced by reactive filtering.
    - `SelectedSessionId: BindableReactiveProperty<string?>`.
- **View**
  - `SessionsSidebarView.xaml` rendering the list, binding to search and selection; unread badge visibility based on count.
- **Shell integration**
  - Replace sidebar placeholder with this view; publish selection as `IObservable<string?>` for the shell.

Outcome: Searchable, reactive sessions list; selection propagates to shell.

---

## Chunk 05 — Chat feature (conversation area)

- **Models/Service**
  - `ChatMessage { Id, Author, Text, Timestamp, IsOwn }`.
  - `IChatHistory` mock returning a few messages per session; append on send.
- **ViewModel**
  - `ChatViewModel` with
    - `Messages: ReadOnlyObservableCollection<ChatMessage>` bound to selected session.
    - `MessageInput: BindableReactiveProperty<string>`.
    - `CanSend: BindableReactiveProperty<bool>` via `MessageInput.Select(text => !string.IsNullOrWhiteSpace(text))`.
    - `SendCommand: AsyncRelayCommand` appending to history and clearing input.
- **View**
  - `ChatView.xaml`: messages list with simple bubbles, input row, send button bound to `CanSend`.
- **Shell routing**
  - Content region swaps to `ChatView` when a session is selected; otherwise shows welcome panel.

Outcome: End‑to‑end local chat flow with mock data and reactive send behavior.

---

## Chunk 06 — Background services (hosted)

- **Hosted services**
  - `MessageBusService : IHostedService` (in-memory, placeholder) to simulate inbound messages.
  - `DecryptionService : IHostedService` placeholder to represent CPU-bound work off the UI thread.
  - `NetworkPollingService : IHostedService` placeholder for future gRPC integration.
- **Interop**
  - Expose `IObservable<InboundMessage>` from bus; `ChatViewModel` subscribes to update `Messages`.

Outcome: Demonstrates Generic Host background processing without UI freezes.

---

## Chunk 07 — Configuration and environments

- Add `appsettings.json` with sections:
  - `Ui: NerdFont`.
  - `Services: DhtPolling: IntervalMs`.
- Bind via `IOptions<T>` and inject into Shell and services.
- Load `appsettings.Development.json` if present; ensure logging to console/debug.

Outcome: App settings are changeable without code edits.

---

## Chunk 08 — Accessibility and UX polish

- Keyboard navigation for sidebar and input.
- Focus visuals and high‑contrast brush set.
- Typography scaling using dynamic resources and `TextElement.FontSize` base.
- Animations using `VisualStateManager` for list selection and view transitions.

Outcome: Meets stated ADA/WCAG goals within WPF constraints.

---

## Chunk 09 — Testing setup (TDD harness)

- Create `Desktop.Wpf.Tests` project.
- Add tests for
  - `SessionsSidebarViewModel` filtering and selection behavior.
  - `ChatViewModel` `CanSend` logic and `SendCommand` effects.
- Use R3 `TestScheduler` or time‑free tests for reactive chains.

Outcome: Green tests validating observable behavior (no UI).

---

## Chunk 10 — Packaging and CI hooks

- Embed fonts/icons and mark `Resource` build action.
- Ensure single‑file plan documented in `readme.md` Getting Started uses `dotnet run --project Desktop.Wpf`.

Outcome: Repeatable builds with basic CI and assets packaged.

---

## Notes

- All domain integrations (crypto, sessions, networking) remain mocked here; real implementations will bind through the Application layer later.
- Maintain the `.Value` binding pattern per readme. Keep view logic minimal; all behavior in ViewModels.

