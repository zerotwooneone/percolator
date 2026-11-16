// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Percolator.Desktop.Features.Transports.ViewModels;

namespace Percolator.Desktop.Features.Transports.Views;

public partial class ShareInvitationDialog : Window
{
    private ShareInvitationDialogViewModel? _wired;

    public ShareInvitationDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired is not null)
        {
            _wired.RequestClose -= OnRequestClose;
            _wired.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _wired = DataContext as ShareInvitationDialogViewModel;
        if (_wired is not null)
        {
            _wired.RequestClose += OnRequestClose;
            _wired.PropertyChanged += OnViewModelPropertyChanged;
            UpdateQrCodeImage(_wired.QrCodePngBytes);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShareInvitationDialogViewModel.QrCodePngBytes) && _wired is not null)
        {
            UpdateQrCodeImage(_wired.QrCodePngBytes);
        }
    }

    private void UpdateQrCodeImage(byte[]? pngBytes)
    {
        if (pngBytes is not null && pngBytes.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                QrCodeImage.Source = new Bitmap(ms);
            }
            catch
            {
                QrCodeImage.Source = null;
            }
        }
        else
        {
            QrCodeImage.Source = null;
        }
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        Close();
    }
}
