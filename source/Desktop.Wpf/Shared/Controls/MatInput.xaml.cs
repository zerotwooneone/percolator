using System.Windows;
using System.Windows.Controls;
using R3;

namespace Desktop.Wpf.Shared.Controls;

public partial class MatInput : UserControl
{
    public MatInput()
    {
        InitializeComponent();
        _cleanup = new CompositeDisposable();
        // Wire debounced propagation when loaded
        Loaded += (_, __) => ConfigureDebounce();
        Unloaded += (_, __) => _cleanup.Dispose();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MatInput),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(MatInput), new PropertyMetadata(string.Empty));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(object), typeof(MatInput));

    public object? Prefix
    {
        get => GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(object), typeof(MatInput));

    public object? Suffix
    {
        get => GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public static readonly DependencyProperty DebounceDelayProperty = DependencyProperty.Register(
        nameof(DebounceDelay), typeof(TimeSpan), typeof(MatInput), new PropertyMetadata(TimeSpan.FromMilliseconds(200), OnDebounceDelayChanged));

    public TimeSpan DebounceDelay
    {
        get => (TimeSpan)GetValue(DebounceDelayProperty);
        set => SetValue(DebounceDelayProperty, value);
    }

    public static readonly DependencyProperty DebouncedTextProperty = DependencyProperty.Register(
        nameof(DebouncedText), typeof(string), typeof(MatInput), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string DebouncedText
    {
        get => (string)GetValue(DebouncedTextProperty);
        set => SetValue(DebouncedTextProperty, value);
    }

    private static void OnDebounceDelayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MatInput mi) mi.ConfigureDebounce();
    }

    private void ConfigureDebounce()
    {
        _cleanup.Dispose();
        _cleanup = new CompositeDisposable();
        // Mirror Text into DebouncedText with debounce using a Subject pipeline
        _textSubject ??= new Subject<string>();
        _textSubscription = _textSubject
            .Debounce(DebounceDelay)
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(value => DebouncedText = value)
            .AddTo(_cleanup);
        // push current value to initialize
        _textSubject.OnNext(Text);
    }

    private CompositeDisposable _cleanup;
    private IDisposable? _textSubscription;
    private Subject<string>? _textSubject;

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MatInput mi)
        {
            mi._textSubject ??= new Subject<string>();
            mi._textSubject.OnNext((string?)e.NewValue ?? string.Empty);
        }
    }
}
