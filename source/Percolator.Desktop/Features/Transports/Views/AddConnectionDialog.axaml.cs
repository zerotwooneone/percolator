// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;

namespace Percolator.Desktop.Features.Transports.Views;

public partial class AddConnectionDialog : Window, IDialogWindow<AddConnectionDialogResult>
{
    private AddConnectionDialogViewModel? _wired;

    public AddConnectionDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public AddConnectionDialogResult? DialogResult =>
        (DataContext as AddConnectionDialogViewModel)?.Result;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired is not null)
        {
            _wired.RequestClose -= OnRequestClose;
        }

        _wired = DataContext as AddConnectionDialogViewModel;
        if (_wired is not null)
        {
            _wired.RequestClose += OnRequestClose;
        }
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        Close();
    }
}
