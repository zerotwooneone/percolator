using System;
using CommunityToolkit.Mvvm.ComponentModel;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal abstract class ViewModelBase : ObservableObject, IDisposable
{
    protected DisposableBag Disposables;

    public virtual void Dispose()
    {
        Disposables.Dispose();
    }
}
