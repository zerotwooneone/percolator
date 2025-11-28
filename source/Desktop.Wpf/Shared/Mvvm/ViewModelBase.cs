using System;
using R3;

namespace Desktop.Wpf.Shared.Mvvm;

public abstract class ViewModelBase : IDisposable
{
    private bool _disposed;

    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }
}
