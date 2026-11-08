using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Percolator.Simulator.Features.Logging;

public partial class LogDockView : UserControl
{
    private TextBox? _textBox;

    public LogDockView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _textBox = this.FindControl<TextBox>("LogTextBox");
        if (_textBox != null)
        {
            _textBox.PropertyChanged += OnTextBoxPropertyChanged;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (_textBox != null)
        {
            _textBox.PropertyChanged -= OnTextBoxPropertyChanged;
        }
    }

    private void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && _textBox != null)
        {
            // Only auto-scroll to the end if the user does NOT have an active text selection
            if (_textBox.SelectionStart == _textBox.SelectionEnd)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_textBox != null && _textBox.SelectionStart == _textBox.SelectionEnd)
                    {
                        var len = _textBox.Text?.Length ?? 0;
                        _textBox.CaretIndex = len;
                    }
                }, DispatcherPriority.Background);
            }
        }
    }
}
