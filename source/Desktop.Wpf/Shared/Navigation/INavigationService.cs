using System;
using R3;

namespace Desktop.Wpf.Shared.Navigation;

public interface INavigationService
{
    Observable<object?> ViewStream { get; }
    void Navigate(object? view);
}
