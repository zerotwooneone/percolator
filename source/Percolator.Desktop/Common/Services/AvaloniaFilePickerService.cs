// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = false, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
                if (topLevel?.StorageProvider is { } storage)
                {
                    var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = title,
                        AllowMultiple = allowMultiple
                    }).ConfigureAwait(false);

                    var paths = new List<string>();
                    foreach (var file in files)
                    {
                        var localPath = file.TryGetLocalPath();
                        if (!string.IsNullOrEmpty(localPath))
                        {
                            paths.Add(localPath);
                        }
                    }
                    return (IReadOnlyList<string>)paths;
                }
            }
            return Array.Empty<string>();
        });
    }

    public async Task<string?> PickFolderAsync(string title, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
                if (topLevel?.StorageProvider is { } storage)
                {
                    var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = title,
                        AllowMultiple = false
                    }).ConfigureAwait(false);

                    var first = folders.FirstOrDefault();
                    return first?.TryGetLocalPath();
                }
            }
            return null;
        });
    }
}
