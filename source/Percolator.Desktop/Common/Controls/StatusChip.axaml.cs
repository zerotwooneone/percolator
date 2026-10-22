using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Percolator.Desktop.Common.Controls;

public enum StatusChipVariant
{
    Normal,
    DirectRoute,
    RelayedRoute,
    Success,
    Warning,
    Error,
    Neutral
}

public partial class StatusChip : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));

    public static readonly StyledProperty<NerdGlyph> IconGlyphProperty =
        AvaloniaProperty.Register<StatusChip, NerdGlyph>(nameof(IconGlyph), NerdGlyph.None);

    public static readonly StyledProperty<StatusChipVariant> VariantProperty =
        AvaloniaProperty.Register<StatusChip, StatusChipVariant>(nameof(Variant), StatusChipVariant.Normal);

    public static readonly DirectProperty<StatusChip, bool> HasIconProperty =
        AvaloniaProperty.RegisterDirect<StatusChip, bool>(nameof(HasIcon), o => o.HasIcon);

    public static readonly DirectProperty<StatusChip, IBrush> ChipBackgroundBrushProperty =
        AvaloniaProperty.RegisterDirect<StatusChip, IBrush>(nameof(ChipBackgroundBrush), o => o.ChipBackgroundBrush);

    public static readonly DirectProperty<StatusChip, IBrush> ChipBorderBrushProperty =
        AvaloniaProperty.RegisterDirect<StatusChip, IBrush>(nameof(ChipBorderBrush), o => o.ChipBorderBrush);

    public static readonly DirectProperty<StatusChip, IBrush> ChipForegroundBrushProperty =
        AvaloniaProperty.RegisterDirect<StatusChip, IBrush>(nameof(ChipForegroundBrush), o => o.ChipForegroundBrush);

    private bool _hasIcon;
    private IBrush _chipBackgroundBrush = Brushes.Transparent;
    private IBrush _chipBorderBrush = Brushes.Transparent;
    private IBrush _chipForegroundBrush = Brushes.White;

    public StatusChip()
    {
        InitializeComponent();
        UpdateStyles();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public NerdGlyph IconGlyph
    {
        get => GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public StatusChipVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public bool HasIcon
    {
        get => _hasIcon;
        private set => SetAndRaise(HasIconProperty, ref _hasIcon, value);
    }

    public IBrush ChipBackgroundBrush
    {
        get => _chipBackgroundBrush;
        private set => SetAndRaise(ChipBackgroundBrushProperty, ref _chipBackgroundBrush, value);
    }

    public IBrush ChipBorderBrush
    {
        get => _chipBorderBrush;
        private set => SetAndRaise(ChipBorderBrushProperty, ref _chipBorderBrush, value);
    }

    public IBrush ChipForegroundBrush
    {
        get => _chipForegroundBrush;
        private set => SetAndRaise(ChipForegroundBrushProperty, ref _chipForegroundBrush, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconGlyphProperty)
        {
            HasIcon = IconGlyph != NerdGlyph.None;
        }
        else if (change.Property == VariantProperty)
        {
            UpdateStyles();
        }
    }

    private void UpdateStyles()
    {
        HasIcon = IconGlyph != NerdGlyph.None;

        switch (Variant)
        {
            case StatusChipVariant.DirectRoute:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xBC, 0xD4));
                ChipBorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
                break;

            case StatusChipVariant.RelayedRoute:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x98, 0x00));
                ChipBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
                break;

            case StatusChipVariant.Success:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0xAF, 0x50));
                ChipBorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                break;

            case StatusChipVariant.Warning:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xC1, 0x07));
                ChipBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
                break;

            case StatusChipVariant.Error:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xF4, 0x43, 0x36));
                ChipBorderBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
                break;

            case StatusChipVariant.Neutral:
            default:
                ChipBackgroundBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                ChipBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                ChipForegroundBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                break;
        }
    }
}
