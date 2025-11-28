using System;
using System.ComponentModel;

namespace Desktop.Wpf.Shared.Navigation;

public interface INavigationService : INotifyPropertyChanged
{
    object? CurrentView { get; }
    void Navigate(object? view);
}
