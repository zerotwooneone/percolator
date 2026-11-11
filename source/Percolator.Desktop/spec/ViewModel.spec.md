<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->

# Avalonia ViewModel Specification (`ViewModel.spec.md`)

This specification governs all ViewModel classes, presentation logic, and unit tests across `Percolator.Desktop`. All unit testing must adhere to the core principles in [`source/unit-testing.md`](../../unit-testing.md).

Any file governed by this specification MUST include this header comment on line 1:
```csharp
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
```

---

## 1. Quick Rules & Invariants

| ID | Category | MUST DO (Mandatory) | MUST NOT DO (Strictly Prohibited) |
| :-: | :--- | :--- | :--- |
| **[VM-001]** | **Base Class Lifecycle** | Inherit from `ViewModelBase` (`ObservableObject` + `DisposableBag Bag` + `IDisposable`). Override `DisposeCore()` for custom teardown. | MUST NOT inherit from raw `ObservableObject` without disposable lifecycle management. |
| **[VM-002]** | **Absolute UI Isolation** | Keep ViewModels 100% UI-agnostic. ViewModels communicate via primitives, flat records, and commands. | MUST NOT import `Avalonia.Controls`, `Avalonia.Visual`, or reference `Window` / `Control` instances or raw pixel dimensions in ViewModels. |
| **[VM-003]** | **Mandatory Constructor DI** | Declare all required dependencies as non-nullable constructor parameters (`dep ?? throw new ArgumentNullException()`). | MUST NOT declare nullable required parameters (`dep = null`) or default mock fallbacks to take shortcuts in tests. |
| **[VM-004]** | **Service Locator Prohibition** | Inject domain, application, or presentation dependencies explicitly. | MUST NOT inject `IServiceProvider`, `App.Services`, or call `sp.GetRequiredService<T>()` inside a ViewModel. |
| **[VM-005]** | **CommunityToolkit Generators** | Use `[ObservableProperty]` for state fields and `[RelayCommand]` for actions. Use `[NotifyCanExecuteChangedFor(nameof(...))]` to signal command state transitions. | MUST NOT write hand-rolled `INotifyPropertyChanged` boilerplate or manual `ICommand` implementations. MUST NOT call `OnPropertyChanged()` manually inside generated property setters. |
| **[VM-006]** | **CQRS Read Projections** | Populate UI collections and lists via dedicated query services returning flat, immutable DTO snapshots. | MUST NOT query write-side domain aggregate repositories or mutate domain aggregate entities directly within ViewModels. |
| **[VM-007]** | **Time Virtualization** | Inject `TimeProvider` or `IDateTimeProvider` for all timestamps, delays, or interval clocks. | MUST NOT invoke `DateTime.UtcNow`, `DateTime.Now`, or unvirtualized `Task.Delay(N)` in ViewModels. |
| **[VM-008]** | **Reactive Stream Anchoring** | Author reactive pipelines according to [`R3.spec.md`](./R3.spec.md) and anchor all subscriptions via `.AddTo(ref Bag)` ([R3-005]). | MUST NOT create unanchored observable subscriptions or pass `Bag` by value. |
| **[VM-009]** | **Child ViewModel Factories** | Instantiate dynamic child ViewModels (e.g. list items, dialogs) via injected factory delegates (`Func<TChildViewModel>`). | MUST NOT pass ambient `IServiceProvider` to child ViewModels or instantiate children via `new` with transitive parent dependencies. |
| **[VM-010]** | **State-Based Testing** | Unit tests MUST assert observable public state, collection contents, and domain outcomes. | MUST NOT assert internal mock call counts (`Received(1)`, `Verify()`) or method call sequences. |
| **[VM-011]** | **Zero-Value Test Elimination** | Delete or refuse to write brittle mock verification tests, compiler property tests, `Task.Delay` tests, and container count assertions. | MUST NOT commit tests that verify framework mechanics instead of system requirements. |
| **[VM-012]** | **Test Runner Separation** | Execute 90%+ of ViewModel tests as standard in-memory tests (`[Test]` in NUnit / `[Fact]` in xUnit). Reserve headless tests (`[AvaloniaTest]` / `[AvaloniaFact]`) strictly for UI/dispatcher integration. | MUST NOT run headless UI tests for pure in-memory state transition and command logic. |
| **[VM-013]** | **Mandatory Logic Testing vs. Pure State** | Any ViewModel containing significant business logic (state machine transitions, asynchronous commands, custom validation, or reactive pipelines) MUST have an associated unit test suite in `Percolator.Desktop.Tests` complying with [`unit-testing.md`](../../unit-testing.md). | MUST NOT write ritualistic unit tests for simple presentation models or DTOs containing only auto-properties and zero logic (violates token economy and provides zero regression value). |
| **[VM-014]** | **Dialog & Confirmation Ingress** | Request user confirmations, modal dialogs, or platform file pickers via injected service abstractions (e.g. `IDialogService`, `IWindowManager`) returning a `Task<TResult>`. | MUST NOT instantiate Windows directly, call `ShowDialog()`, or reference visual roots in ViewModels. |
| **[VM-015]** | **Computed Properties vs. Converters** | Expose UI state transformations (e.g. `HasErrors`, `FormattedCount`, `IsActionVisible`) as computed ViewModel properties or R3 projections. | MUST NOT create single-use `IValueConverter` classes for simple boolean or formatting transformations that can be tested in pure C#. |
| **[VM-016]** | **Form Validation (`INotifyDataErrorInfo`)** | Implement input form validation by inheriting from CommunityToolkit’s `ObservableValidator` and applying standard validation attributes (`[Required]`, `[MinLength]`). | MUST NOT hand-roll ad-hoc boolean error flags when Avalonia natively renders validation rings and tooltips for `INotifyDataErrorInfo`. |

---

## 2. High-Value, Low-Brittle Testing Discipline

All unit testing across `Percolator.Desktop.Tests` is authoritatively governed by the repository standards in [`source/unit-testing.md`](../../unit-testing.md) (*DIRECTIVE: SYSTEM STANDARDS FOR ROBUST UNIT TESTING*).

### 2.1 Testing Significance Threshold ([VM-013])

To maximize token economy and avoid brittle test sprawl, we strictly distinguish between **significant logic** and **pure state**:

| Category | Significance Criteria | Unit Testing Requirement |
| :--- | :--- | :--- |
| **Significant Logic ViewModel** | Coordinates async commands (`[RelayCommand]`), drives state machine transitions, performs input validation, or manages reactive stream pipelines. | **MANDATORY**: Must have comprehensive, state-based unit tests in `Percolator.Desktop.Tests` (e.g. `MainWindowViewModelTests`, `IdentityGateViewModelTests`). |
| **Pure State / DTO ViewModel** | Flat presentation records, item summaries, or value wrappers containing auto-properties with zero commands, zero reactive pipelines, and zero business logic. | **STRICTLY PROHIBITED**: Do not write tests that merely verify `vm.Property = x; Assert.Equal(x, vm.Property);`. This tests compiler auto-properties, wasting tokens with zero defect protection. |

### 2.2 State-Based vs. Interaction-Based Testing ([VM-010])
ViewModel unit tests MUST verify **observable state and domain outcomes**, never internal method calls or mock invocation sequences:

| Brittle Interaction-Based Testing (STRICTLY PROHIBITED) | High-Value State-Based Testing (MANDATORY) |
| :--- | :--- |
| Asserting internal call counts: `mockRepo.Received(1).Save(...)` | Asserting public state: `vm.Status.Should().Be(Completed)` |
| Mocking deep interface chains: `when(m.A().B().C()).then(...)` | Using lightweight **in-memory Fakes** (`FakeContactQueryService`) |
| Breaks on every internal refactor even when UI works perfectly | Resilient: logic can refactor completely while tests verify real outcomes |

### 2.3 Tests to DELETE Immediately (or Never Write) ([VM-011])
1. **DELETE: Mock Call Verification Tests (`Received(N)`, `Verify(Times.Once)`)**:
   * *Bad*: `mockService.Received(1).NotifyUser(Arg.Any<string>());`
   * *Why*: Tests internal implementation wiring rather than user outcomes. Assert that `vm.Notifications` contains the expected item instead.
2. **DELETE: Boilerplate Property Tests**:
   * *Bad*: `vm.SearchText = "abc"; Assert.Equal("abc", vm.SearchText);`
   * *Why*: Tests the C# compiler and `[ObservableProperty]` generator. Provides zero regression protection.
3. **DELETE: Real-Time Delay Tests (`Task.Delay(N)`)**:
   * *Bad*: `await Task.Delay(200); Assert.True(vm.IsLoaded);`
   * *Why*: Flaky under CI load and machine variance. Use `FakeTimeProvider.Advance(TimeSpan)` or deterministic signal awaiting.
4. **DELETE: Magic Container Count Assertions**:
   * *Bad*: `services.Should().HaveCount(14);`
   * *Why*: Breaks whenever any unrelated service is registered. Assert specific capability containment (`services.Should().Contain(...)`).

### 2.4 Test Runner Separation ([VM-012])
* **Standard Unit Tests (`[Test]` / `[Fact]`)**:
  * Tests commands, property updates, validation, and state transitions in memory. Fast, deterministic, zero UI platform overhead.
* **Headless UI Tests (`[AvaloniaTest]` / `[AvaloniaFact]`)**:
  * Reserve headless test runners (via `Avalonia.Headless.NUnit` / `XUnit`) strictly for tests verifying **XAML data bindings, control templates, or `Dispatcher.UIThread` marshaling**.

---

## 3. Micro-Diff Examples: Bad vs. Good

### 3.1 Dependency Injection & Service Locator ([VM-003], [VM-004])
```csharp
// ❌ BAD: Service locator + nullable required dependency
public sealed class ChatViewModel : ViewModelBase
{
    public ChatViewModel(IServiceProvider sp, IChatService? chatService = null)
    {
        var repo = sp.GetRequiredService<IChatRepository>(); // VIOLATION: Service locator!
    }
}

// ✅ GOOD: Pure constructor injection with non-nullable parameters
public sealed class ChatViewModel : ViewModelBase
{
    private readonly IChatService _chatService;
    private readonly IChatMessageQueryService _queryService;

    public ChatViewModel(IChatService chatService, IChatMessageQueryService queryService)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
    }
}
```

### 3.2 CommunityToolkit Source Generation ([VM-005])
```csharp
// ❌ BAD: Hand-rolled boilerplate with manual event triggers
public class PersonaViewModel : ViewModelBase
{
    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged(); // VIOLATION: Redundant hand-rolled boilerplate
            }
        }
    }
}

// ✅ GOOD: Source-generated observable properties and partial notifications
public partial class PersonaViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePersonaCommand))]
    private string _displayName = string.Empty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SavePersonaAsync()
    {
        // Execution
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(DisplayName);
}
```

### 3.3 State-Based vs. Brittle Interaction Testing ([VM-010], [VM-011])
```csharp
// ❌ BAD (Brittle): Asserts internal mock invocations; breaks on any internal refactor
[Test]
public async Task SubmitOrder_Calls_Repository_Once()
{
    var mockRepo = new Mock<IOrderRepository>();
    var vm = new OrderViewModel(mockRepo.Object);

    await vm.SubmitCommand.ExecuteAsync(null);

    // BRITTLE: Breaks if method is renamed, batched, or deferred
    mockRepo.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Once);
}

// ✅ GOOD (High-Value): Asserts user-facing observable state using an in-memory fake
[Test]
public async Task SubmitOrder_Transitions_State_To_Completed()
{
    var fakeRepo = new FakeOrderRepository();
    var vm = new OrderViewModel(fakeRepo);

    await vm.SubmitCommand.ExecuteAsync(null);

    // RESILIENT: Tests observable domain state and outcomes
    vm.Status.Should().Be(OrderStatus.Completed);
    vm.ErrorMessage.Should().BeNull();
    fakeRepo.SavedOrders.Should().ContainSingle();
}
```

### 3.4 Modal / Dialog Ingress via Injected Abstractions ([VM-014])
```csharp
// ❌ BAD: ViewModel instantiates and launches an OS window directly
[RelayCommand]
private async Task DeletePersonaAsync()
{
    var dialog = new ConfirmDeleteWindow();
    if (await dialog.ShowDialog<bool>(_mainWindow)) // VIOLATION: UI coupling in ViewModel!
    {
        await _repository.DeleteAsync();
    }
}

// ✅ GOOD: Asynchronous dialog service abstraction injected via constructor
[RelayCommand]
private async Task DeletePersonaAsync()
{
    var confirmed = await _dialogService.ConfirmAsync(
        "Delete Persona", 
        "Are you sure you want to delete this cryptographic identity?");

    if (confirmed)
    {
        await _personaService.DeleteAsync(SelectedPersona.Id);
    }
}
```

### 3.5 Computed Properties vs. Value Converters ([VM-015])
```csharp
// ❌ BAD: Creating a separate, untestable IValueConverter for simple boolean logic
public class HasMessagesToBoolConverter : IValueConverter { ... }

// ✅ GOOD: Testable computed property directly on the ViewModel
public partial class ConversationViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessages))]
    private int _messageCount;

    public bool HasMessages => MessageCount > 0;
}
```

---

## 4. Citations & Verified Sources

* **Repository Testing Directive**: [`source/unit-testing.md`](../../unit-testing.md). Authoritative project standard for robust unit testing, token economy, AAA structure, and the prohibition against testing compiler auto-properties.
* **CommunityToolkit.Mvvm Overview**: [Microsoft Learn – MVVM Toolkit](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/). Canonical reference for `ObservableObject`, `ObservableValidator`, `[ObservableProperty]`, and `[RelayCommand]` source generators.
* **CommunityToolkit Source Generators**: [Microsoft Learn – MVVM Source Generators](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/observableproperty). Details `[NotifyPropertyChangedFor]` and `[NotifyCanExecuteChangedFor]` code generation.
* **Avalonia Headless Testing**: [Avalonia Documentation – Headless Testing](https://docs.avaloniaui.net/docs/concepts/headless). Specifies when to use standard in-memory tests vs `[AvaloniaTest]` / `[AvaloniaFact]` for dispatcher-bound views.
* **Martin Fowler – Mocks Aren't Stubs**: [MartinFowler.com – Mocks Aren't Stubs](https://martinfowler.com/articles/mocksArentStubs.html). Definitive architectural treatise establishing why state-based verification produces resilient test suites while interaction verification creates brittle tests.
* **Google Testing Best Practices**: [Google Testing Blog – Testing State vs. Testing Interactions](https://testing.googleblog.com/2013/03/testing-on-toilet-testing-state-not.html). Documents maintenance overhead and false test alarms caused by interaction mocking.
* **Microsoft Clean Architecture Guidelines**: [Common Web Application Architectures – Clean Architecture](https://learn.microsoft.com/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures). Establishes UI-agnostic presentation layers and CQRS read model boundaries.
