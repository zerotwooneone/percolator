using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Percolator.Desktop.Common.Controls;

public partial class PeerAvatar : UserControl
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<PeerAvatar, string?>(nameof(DisplayName));

    public static readonly StyledProperty<string?> IdentityKeyHexProperty =
        AvaloniaProperty.Register<PeerAvatar, string?>(nameof(IdentityKeyHex));

    public static readonly StyledProperty<PeerPresenceStatus> PresenceStatusProperty =
        AvaloniaProperty.Register<PeerAvatar, PeerPresenceStatus>(nameof(PresenceStatus), PeerPresenceStatus.Offline);

    public static readonly StyledProperty<double> AvatarSizeProperty =
        AvaloniaProperty.Register<PeerAvatar, double>(nameof(AvatarSize), 40.0);

    public static readonly StyledProperty<bool> ShowPresenceProperty =
        AvaloniaProperty.Register<PeerAvatar, bool>(nameof(ShowPresence), true);

    public static readonly DirectProperty<PeerAvatar, string> InitialsProperty =
        AvaloniaProperty.RegisterDirect<PeerAvatar, string>(nameof(Initials), o => o.Initials);

    public static readonly DirectProperty<PeerAvatar, IBrush> PresenceBrushProperty =
        AvaloniaProperty.RegisterDirect<PeerAvatar, IBrush>(nameof(PresenceBrush), o => o.PresenceBrush);

    public static readonly DirectProperty<PeerAvatar, double> PresenceDotSizeProperty =
        AvaloniaProperty.RegisterDirect<PeerAvatar, double>(nameof(PresenceDotSize), o => o.PresenceDotSize);

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
    private static readonly IBrush DirectBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4)); // Cyan
    private static readonly IBrush RelayedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // Orange
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // Grey

    private string _initials = "?";
    private IBrush _presenceBrush = OfflineBrush;
    private double _presenceDotSize = 12.0;

    public PeerAvatar()
    {
        InitializeComponent();
        UpdateCalculatedProperties();
    }

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string? IdentityKeyHex
    {
        get => GetValue(IdentityKeyHexProperty);
        set => SetValue(IdentityKeyHexProperty, value);
    }

    public PeerPresenceStatus PresenceStatus
    {
        get => GetValue(PresenceStatusProperty);
        set => SetValue(PresenceStatusProperty, value);
    }

    public double AvatarSize
    {
        get => GetValue(AvatarSizeProperty);
        set => SetValue(AvatarSizeProperty, value);
    }

    public bool ShowPresence
    {
        get => GetValue(ShowPresenceProperty);
        set => SetValue(ShowPresenceProperty, value);
    }

    public string Initials
    {
        get => _initials;
        private set => SetAndRaise(InitialsProperty, ref _initials, value);
    }

    public IBrush PresenceBrush
    {
        get => _presenceBrush;
        private set => SetAndRaise(PresenceBrushProperty, ref _presenceBrush, value);
    }

    public double PresenceDotSize
    {
        get => _presenceDotSize;
        private set => SetAndRaise(PresenceDotSizeProperty, ref _presenceDotSize, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DisplayNameProperty)
        {
            UpdateInitials();
        }
        else if (change.Property == PresenceStatusProperty)
        {
            UpdatePresenceBrush();
        }
        else if (change.Property == AvatarSizeProperty)
        {
            PresenceDotSize = Math.Max(8.0, AvatarSize * 0.3);
            FontSize = Math.Max(10.0, AvatarSize * 0.4);
        }
    }

    private void UpdateCalculatedProperties()
    {
        UpdateInitials();
        UpdatePresenceBrush();
        PresenceDotSize = Math.Max(8.0, AvatarSize * 0.3);
        FontSize = Math.Max(10.0, AvatarSize * 0.4);
    }

    private void UpdateInitials()
    {
        var name = DisplayName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Initials = "?";
            return;
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            Initials = parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        }
        else
        {
            Initials = $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
        }
    }

    private void UpdatePresenceBrush()
    {
        PresenceBrush = PresenceStatus switch
        {
            PeerPresenceStatus.Online => OnlineBrush,
            PeerPresenceStatus.Direct => DirectBrush,
            PeerPresenceStatus.Relayed => RelayedBrush,
            _ => OfflineBrush
        };
    }
}
