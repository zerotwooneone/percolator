using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.IdentityGate.ViewModels;

public partial class CreateIdentityDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public event Action<string>? Confirmed;
    public event Action? Cancelled;

    public bool CanCreate => !string.IsNullOrWhiteSpace(DisplayName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ErrorMessage = "Display name cannot be empty.";
            return;
        }

        Confirmed?.Invoke(DisplayName.Trim());
    }

    [RelayCommand]
    private void Cancel()
    {
        Cancelled?.Invoke();
    }
}
