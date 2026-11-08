using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

internal sealed partial class UiLogItem : ObservableObject
{
    public DateTimeOffset TimestampUtc { get; }
    public LogLevel Level { get; }
    public string? NodeName { get; }
    public string Category { get; }
    public string Message { get; }
    public string BadgeColor { get; }
    public string FormattedText { get; }
    private readonly Func<Task>? _copyAllAction;
    private readonly IStatusToastService? _toastService;

    public UiLogItem(
        DateTimeOffset timestampUtc,
        LogLevel level,
        string? nodeName,
        string category,
        string message,
        string badgeColor,
        string formattedText,
        Func<Task>? copyAllAction = null,
        IStatusToastService? toastService = null)
    {
        TimestampUtc = timestampUtc;
        Level = level;
        NodeName = nodeName;
        Category = category;
        Message = message;
        BadgeColor = badgeColor;
        FormattedText = formattedText;
        _copyAllAction = copyAllAction;
        _toastService = toastService;
    }

    [RelayCommand]
    public async Task CopyLineAsync()
    {
        await ClipboardHelper.SetClipboardTextAsync(FormattedText, "Copied log line to clipboard.", _toastService);
    }

    [RelayCommand]
    public async Task CopyMessageAsync()
    {
        await ClipboardHelper.SetClipboardTextAsync(Message, "Copied log message to clipboard.", _toastService);
    }

    [RelayCommand]
    public async Task CopyAllLogsAsync()
    {
        if (_copyAllAction != null)
        {
            await _copyAllAction();
        }
    }
}

internal sealed partial class LogDockViewModel : ViewModelBase
{
    private const int MaxLogCapacity = 500;

    private readonly ISimulatorLogSink _logSink;
    private readonly IStatusToastService? _toastService;
    private readonly BatchAccumulator<UiLogItem> _logAccumulator;

    [ObservableProperty]
    private int _selectedLogLevelIndex = 2; // Default Information

    public ObservableCollection<UiLogItem> LogEntries { get; } = new();

    public LogDockViewModel(ISimulatorLogSink logSink, IStatusToastService? toastService = null)
    {
        _logSink = logSink;
        _toastService = toastService;

        // 30 Hz UI batching accumulator with leading-edge immediate dispatch
        _logAccumulator = new BatchAccumulator<UiLogItem>(
            TimeSpan.FromMilliseconds(33),
            maxBatchSize: 100,
            flushAction: batch =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var excess = (LogEntries.Count + batch.Count) - MaxLogCapacity;
                    while (excess > 0 && LogEntries.Count > 0)
                    {
                        LogEntries.RemoveAt(0);
                        excess--;
                    }

                    foreach (var item in batch)
                    {
                        LogEntries.Add(item);
                    }
                });
            });

        _logAccumulator.AddTo(ref Disposables);

        // Ingestion-time forward filter + UI batching
        _logSink.LogStream
            .Subscribe(entry =>
            {
                var minLevel = GetSelectedLogLevel();
                if (entry.Level < minLevel) return;

                var uiItem = FormatUiLogItem(entry);
                _logAccumulator.Enqueue(uiItem);
            })
            .AddTo(ref Disposables);
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

    private UiLogItem FormatUiLogItem(SimulatorLogEntry entry)
    {
        var badgeColor = entry.Level switch
        {
            LogLevel.Trace => "#888888",
            LogLevel.Debug => "#3A96DD",
            LogLevel.Information => "#4CAF50",
            LogLevel.Warning => "#FF9800",
            LogLevel.Error or LogLevel.Critical => "#F44336",
            _ => "#AAAAAA"
        };

        var nodePart = string.IsNullOrEmpty(entry.NodeName) ? "" : $"[{entry.NodeName}] ";
        var formatted = $"[{entry.TimestampUtc:HH:mm:ss.fff}] [{entry.Level}] {nodePart}{entry.Category}: {entry.Message}";
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            formatted += $"{Environment.NewLine}  Ex: {entry.Exception}";
        }

        return new UiLogItem(
            entry.TimestampUtc,
            entry.Level,
            entry.NodeName,
            entry.Category,
            entry.Message,
            badgeColor,
            formatted,
            CopyAllLogsAsync,
            _toastService);
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }

    [RelayCommand]
    public async Task CopyAllLogsAsync()
    {
        if (LogEntries.Count == 0)
        {
            _toastService?.ShowToast("Log Viewer", "No log entries to copy.", ToastSeverity.Informational);
            return;
        }

        var allText = string.Join(Environment.NewLine, LogEntries.Select(e => e.FormattedText));
        await ClipboardHelper.SetClipboardTextAsync(allText, $"Copied {LogEntries.Count} log lines to clipboard.", _toastService);
    }
}
