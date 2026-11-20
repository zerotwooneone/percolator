// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Percolator.Desktop.Features.Startup.ViewModels;

namespace Percolator.Desktop.Features.Startup.Views;

public partial class VaultUnlockView : UserControl
{
    private static readonly Animation ShakeAnimation = BuildShakeAnimation();

    public VaultUnlockView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Complies with [CB-003]: autofocus the passphrase field once attached.
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VaultUnlockViewModel vm)
        {
            vm.PropertyChanged += ViewModel_PropertyChanged;
        }

        PasswordInput.Focus();
    }

    // Complies with [CB-004]: Loaded subscription paired with Unloaded cleanup.
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VaultUnlockViewModel vm)
        {
            vm.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultUnlockViewModel.FailedAttemptNonce))
        {
            // View-only failed-attempt feedback ([CB-001]); the VM owns no animation state.
            _ = ShakeAnimation.RunAsync(ShakeTarget);
        }
    }

    private static Animation BuildShakeAnimation()
    {
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(320) };
        double[] offsets = { 0, -8, 8, -5, 4, 0 };
        for (var i = 0; i < offsets.Length; i++)
        {
            var frame = new KeyFrame { Cue = new Cue(i / (double)(offsets.Length - 1)) };
            frame.Setters.Add(new Setter(TranslateTransform.XProperty, offsets[i]));
            animation.Children.Add(frame);
        }

        return animation;
    }
}
