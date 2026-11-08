using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Percolator.Simulator.Features.Logging;

public partial class LogDockView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private LogDockViewModel? _attachedVm;

    public LogDockView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _scrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
        AttachViewModel();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewModel();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        DetachViewModel();
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        if (DataContext is LogDockViewModel vm)
        {
            _attachedVm = vm;
            vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
        }
    }

    private void DetachViewModel()
    {
        if (_attachedVm != null)
        {
            _attachedVm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
            _attachedVm = null;
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _scrollViewer != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_scrollViewer != null)
                {
                    var isNearBottom = _scrollViewer.Offset.Y >= _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - 40;
                    if (isNearBottom || _scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height)
                    {
                        _scrollViewer.ScrollToEnd();
                    }
                }
            }, DispatcherPriority.Background);
        }
    }
}
