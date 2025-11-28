# Percolator Chat 🔒✨

**Percolator Chat** is a next-generation WPF desktop application designed to bring the security of the Signal Protocol to a desktop environment without compromising on aesthetics or accessibility.

Built with a rigorous **Test-Driven Development (TDD)** mindset, Percolator combines the robustness of the **.NET Generic Host** with the reactive power of **R3** to create a highly responsive, observable, and architecturally sound application.

![Build Status](https://img.shields.io/badge/build-passing-brightgreen) ![.NET](https://img.shields.io/badge/.NET-9.0-purple) ![Architecture](https://img.shields.io/badge/Architecture-MVVM%20%2B%20Feature%20Slices-blue) ![License](https://img.shields.io/badge/license-MIT-green)

---

## 🌟 Key Features

* **End-to-End Encryption:** Implements Double Ratchet/X3DH (Signal Protocol) for uncompromising privacy.
* **Modern UI/UX:** A visually stunning interface featuring fluid animations, blur effects, and modern typography.
* **ADA Compliant:** Strictly adheres to WCAG 2.1 AA standards for color contrast, font scaling, and keyboard navigation. Beauty is accessible to everyone.
* **Reactive Core:** Powered by R3 for a glitch-free, event-driven data flow.

---

## 🏗️ Architecture & Tech Stack

Percolator departs from legacy WPF patterns, adopting a modern, web-inspired architectural style.

### 1. Feature-Based Organization (Angular Style)
Instead of grouping files by type (e.g., a giant `ViewModels` folder), we group by **Feature**. This ensures that everything related to a specific domain concept lives together.

**Structure Example:**
```text
/src
  /Features
    /Chat
      - ChatView.xaml
      - ChatViewModel.cs
      - ChatService.cs
      - ChatSessionModel.cs
    /Contacts
      - ContactListView.xaml
      - ContactListViewModel.cs
    /Crypto
      - DoubleRatchetSession.cs
```

### 2. .NET Generic Host
We treat the WPF app like a long-running ASP.NET Core service.
* **Lifecycle Management:** controlled via `IHost`.
* **Dependency Injection:** Native `Microsoft.Extensions.DependencyInjection`.
* **Configuration:** `appsettings.json` and Environment Variables.
* **Background Tasks:** `IHostedService` implementations handle message decryption and network polling in the background without freezing the UI.

### 3. Reactive Programming with R3
We strictly use **R3 (Reactive Extensions for .NET)** for all state management.

* **ViewModels (`IBindableReactiveProperty<T>`):** Exposed to the View. Designed for XAML binding.
* **Models (`IReactiveProperty<T>`):** Used for internal logic and domain state that requires observability but not direct UI binding.

---

## 🧑‍💻 Coding Standards & Patterns

### The Data Binding Pattern
Because we use R3, we do not implement `INotifyPropertyChanged` manually.

**ViewModel:**
```csharp
public class ChatViewModel : IDisposable
{
    // R3 Bindable Property
    public BindableReactiveProperty<string> MessageInput { get; }
    public BindableReactiveProperty<bool> CanSend { get; }

    public ChatViewModel()
    {
        MessageInput = new BindableReactiveProperty<string>("");
        
        // Reactive Logic
        CanSend = MessageInput
            .Select(text => !string.IsNullOrWhiteSpace(text))
            .ToBindableReactiveProperty(false);
    }

    public void Dispose() => Disposable.Dispose(MessageInput, CanSend);
}
```

**XAML (View):**
Note that we bind to `.Value` to access the underlying data.
```xml
<TextBox Text="{Binding MessageInput.Value, UpdateSourceTrigger=PropertyChanged}" />
<Button Command="{Binding SendCommand}" IsEnabled="{Binding CanSend.Value}" />
```

### Styling Rule: Base* + Implicit Defaults

- Define a named base style per control type (e.g., `BaseWindow`, `BaseButton`, `BaseTextBlock`, `BaseTextBox`).
- Create an unnamed implicit style `TargetType="T"` that is `BasedOn="{StaticResource BaseT}"` to apply the base to all controls by default.
- Specialized styles (e.g., `AccentButton`) should also be `BasedOn` the corresponding base.

This ensures a consistent, easily themeable baseline and mirrors the mockup’s dark, high-contrast design.

### Test-Driven Development (TDD)
We follow the **Red, Green, Refactor** cycle strictly.
1.  **Red:** Write a failing unit test for a specific behavior (e.g., "Message queue should persist to disk on shutdown").
2.  **Green:** Write just enough implementation code to pass the test.
3.  **Refactor:** Clean up the code, optimize, and ensure it fits the Feature-Based architecture.

*No feature is merged without accompanying high-value behavioral tests.*

---

## 🎨 UI/UX Philosophy

* **Color Palette:** We use a high-contrast theme where primary actions (Send, Call) use distinct colors that pass contrast checkers against their backgrounds.
* **Motion:** State changes (receiving a message, opening a menu) must be animated. We use `VisualStateManager` and Composition API for smooth 60fps transitions.
* **Typography:** Scaling is handled automatically. All UI elements flow based on content size, never fixed pixels.

---

## 🚀 Getting Started

### Prerequisites
* .NET 9.0 SDK
* Visual Studio 2022 or Rider

### Installation
1.  Clone the repository.
2.  Restore dependencies:
    ```bash
    dotnet restore
    ```
3.  Run the tests to ensure the environment is green:
    ```bash
    dotnet test
    ```
4.  Run the application:
    ```bash
    dotnet run --project src/Desktop.Wpf
    ```

---

## 🤝 Contributing

Please read `CONTRIBUTING.md` for details on our TDD workflow and pull request template. Remember: **Features are defined by their tests.**