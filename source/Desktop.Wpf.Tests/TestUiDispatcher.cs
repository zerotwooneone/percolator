using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Tests;

internal sealed class TestUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(func());
    }
}
