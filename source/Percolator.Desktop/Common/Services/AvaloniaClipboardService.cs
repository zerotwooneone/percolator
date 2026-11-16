using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Percolator.Desktop.Common.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            if (topLevel?.Clipboard is { } clipboard)
            {
                return await clipboard.GetTextAsync().ConfigureAwait(false);
            }
        }
        return null;
    }

    public async Task SetTextAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            if (topLevel?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(false);
            }
        }
    }
}
