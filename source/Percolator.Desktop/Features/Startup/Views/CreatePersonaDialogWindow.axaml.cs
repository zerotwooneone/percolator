// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;

namespace Percolator.Desktop.Features.Startup.Views;

/// <summary>
/// Modal create/edit persona dialog. Code-behind is limited to wiring the ViewModel's
/// <c>RequestClose</c> event to <see cref="Window.Close()"/> ([CB-001]: window lifecycle
/// mechanics only; zero business logic).
/// </summary>
public partial class CreatePersonaDialogWindow : Window, IDialogWindow<PersonaEditorResult>
{
    private CreatePersonaDialogViewModel? _wired;

    public CreatePersonaDialogWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public PersonaEditorResult? DialogResult =>
        (DataContext as CreatePersonaDialogViewModel)?.Result;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired is not null)
        {
            _wired.RequestClose -= OnRequestClose;
        }

        _wired = DataContext as CreatePersonaDialogViewModel;
        if (_wired is not null)
        {
            _wired.RequestClose += OnRequestClose;
        }
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();
}
