using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Logging;

internal sealed record UiLogItem(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string? NodeName,
    string Category,
    string Message,
    string BadgeColor,
    string FormattedText);

internal sealed partial class LogDockViewModel : ViewModelBase
{
    private const int MaxLogCapacity = 500;

    private readonly ISimulatorLogSink _logSink;

    [ObservableProperty]
    private int _selectedLogLevelIndex = 2; // Default Information

    public ObservableCollection<UiLogItem> LogEntries { get; } = new();

    public LogDockViewModel(ISimulatorLogSink logSink)
    {
        _logSink = logSink;

        // Ingestion-time forward filter + UI batching
        _logSink.LogStream
            .Subscribe(entry =>
            {
                var minLevel = GetSelectedLogLevel();
                if (entry.Level < minLevel) return;

                var uiItem = FormatUiLogItem(entry);

                Dispatcher.UIThread.Post(() =>
                {
                    if (LogEntries.Count >= MaxLogCapacity)
                    {
                        LogEntries.RemoveAt(0); // FIFO eviction at 500 items
                    }
                    LogEntries.Add(uiItem);
                });
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

    private static UiLogItem FormatUiLogItem(SimulatorLogEntry entry)
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
            formatted);
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }
}
