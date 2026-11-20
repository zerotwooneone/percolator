// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using R3;

namespace Percolator.Desktop.ViewModels;

/// <summary>
/// Base class for all Desktop ViewModels, combining CommunityToolkit.Mvvm's ObservableValidator
/// (itself an ObservableObject — form VMs get INotifyDataErrorInfo + attribute validation per
/// [VM-016]) with R3's DisposableBag for safe, leak-free reactive stream lifetimes.
/// </summary>
public abstract class ViewModelBase : ObservableValidator, IDisposable
{
    private bool _disposed;
    protected DisposableBag Bag;

    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Bag.Dispose();
        DisposeCore();
        GC.SuppressFinalize(this);
    }
}
