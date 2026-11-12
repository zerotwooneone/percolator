// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// OS shell launcher realization. Prefers the platform <c>ILauncher</c> service and
/// falls back to a shell-executed process start.
/// </summary>
public sealed class LauncherService : ILauncherService
{
    /// <inheritdoc />
    public async Task<bool> OpenDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            var mainWindow = await Dispatcher.UIThread.InvokeAsync(() =>
                (Avalonia.Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow);

            if (mainWindow is TopLevel topLevel)
            {
                var dir = new DirectoryInfo(path);
                if (dir.Exists && await topLevel.Launcher.LaunchDirectoryInfoAsync(dir))
                {
                    return true;
                }
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open directory '{path}': {ex}");
            return false;
        }
    }
}
