// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// FluentAvalonia realization of <see cref="IDialogService"/>. Centralizes all
/// modal construction so dialogs are never built in code-behind ([CB-007]).
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public async Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var host = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                ?? throw new InvalidOperationException("Cannot display a modal dialog without an active main window.");

            var dialog = new ContentDialog
            {
                Title = request.Title,
                Content = request.Message,
                PrimaryButtonText = request.PrimaryButtonText,
                CloseButtonText = request.CloseButtonText,
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync(host);
            return result == ContentDialogResult.Primary ? DialogResult.Primary : DialogResult.None;
        });
    }
}
