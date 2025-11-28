using System;
using R3;

namespace Desktop.Wpf.Features.Shell;

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
