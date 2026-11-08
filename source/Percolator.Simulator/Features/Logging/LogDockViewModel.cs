using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Logging;

internal static class ClipboardHelper
{
    public static async Task SetClipboardTextAsync(string text, string successMessage, IStatusToastService? toastService)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard
                    ?? desktop.Windows.FirstOrDefault(w => w.IsActive)?.Clipboard
                    ?? desktop.Windows.FirstOrDefault()?.Clipboard;

                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(text);
                    toastService?.ShowToast("Log Viewer", successMessage, ToastSeverity.Informational);
                }
            }
        }
        catch (Exception ex)
        {
            toastService?.ShowToast("Clipboard Error", ex.Message, ToastSeverity.Warning);
        }
    }
}

internal sealed partial class LogDockViewModel : ViewModelBase
{
    private const int MaxLogCapacity = 1000;

    private readonly ISimulatorLogSink _logSink;
    private readonly IStatusToastService? _toastService;
    private readonly BatchAccumulator<SimulatorLogEntry> _logAccumulator;
    private readonly List<SimulatorLogEntry> _allEntries = new();
    private readonly StringBuilder _visibleBuffer = new();

    [ObservableProperty]
    private int _selectedLogLevelIndex = 2; // Default Information

    [ObservableProperty]
    private string _logText = string.Empty;

    public LogDockViewModel(ISimulatorLogSink logSink, IStatusToastService? toastService = null)
    {
        _logSink = logSink;
        _toastService = toastService;

        // 30 Hz UI batching accumulator with leading-edge immediate dispatch
        _logAccumulator = new BatchAccumulator<SimulatorLogEntry>(
            TimeSpan.FromMilliseconds(33),
            maxBatchSize: 100,
            flushAction: batch =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProcessBatch(batch);
                });
            });

        _logAccumulator.AddTo(ref Disposables);

        // Ingestion & forward to batch accumulator
        _logSink.LogStream
            .Subscribe(entry =>
            {
                _logAccumulator.Enqueue(entry);
            })
            .AddTo(ref Disposables);
    }

    partial void OnSelectedLogLevelIndexChanged(int value)
    {
        RebuildVisibleText();
    }

    private void ProcessBatch(IReadOnlyList<SimulatorLogEntry> batch)
    {
        _allEntries.AddRange(batch);
        var excess = _allEntries.Count - MaxLogCapacity;
        var needsFullRebuild = false;

        if (excess > 0)
        {
            _allEntries.RemoveRange(0, excess);
            needsFullRebuild = true;
        }

        var minLevel = GetSelectedLogLevel();

        if (needsFullRebuild)
        {
            RebuildVisibleText();
        }
        else
        {
            var addedAny = false;
            foreach (var entry in batch)
            {
                if (entry.Level >= minLevel)
                {
                    if (_visibleBuffer.Length > 0)
                    {
                        _visibleBuffer.AppendLine();
                    }
                    _visibleBuffer.Append(FormatLogLine(entry));
                    addedAny = true;
                }
            }

            if (addedAny)
            {
                LogText = _visibleBuffer.ToString();
            }
        }
    }

    private void RebuildVisibleText()
    {
        _visibleBuffer.Clear();
        var minLevel = GetSelectedLogLevel();

        foreach (var entry in _allEntries)
        {
            if (entry.Level >= minLevel)
            {
                if (_visibleBuffer.Length > 0)
                {
                    _visibleBuffer.AppendLine();
                }
                _visibleBuffer.Append(FormatLogLine(entry));
            }
        }

        LogText = _visibleBuffer.ToString();
    }

    private LogLevel GetSelectedLogLevel() => SelectedLogLevelIndex switch
    {
        0 => LogLevel.Trace,
        1 => LogLevel.Debug,
        2 => LogLevel.Information,
        3 => LogLevel.Warning,
        4 => LogLevel.Error,
        _ => LogLevel.Information
    };

    private static string FormatLogLine(SimulatorLogEntry entry)
    {
        var nodePart = string.IsNullOrEmpty(entry.NodeName) ? "" : $"[{entry.NodeName}] ";
        var formatted = $"[{entry.TimestampUtc:HH:mm:ss.fff}] [{entry.Level}] {nodePart}{entry.Category}: {entry.Message}";
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            formatted += $"{Environment.NewLine}  Ex: {entry.Exception}";
        }
        return formatted;
    }

    [RelayCommand]
    private void ClearLog()
    {
        _allEntries.Clear();
        _visibleBuffer.Clear();
        LogText = string.Empty;
    }

    [RelayCommand]
    public async Task CopyAllLogsAsync()
    {
        if (string.IsNullOrEmpty(LogText))
        {
            _toastService?.ShowToast("Log Viewer", "No log entries to copy.", ToastSeverity.Informational);
            return;
        }

        await ClipboardHelper.SetClipboardTextAsync(LogText, "Copied all logs to clipboard.", _toastService);
    }
}
