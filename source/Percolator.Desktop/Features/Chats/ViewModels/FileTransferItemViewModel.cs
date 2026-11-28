// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Desktop.ViewModels;

public sealed partial class FileTransferItemViewModel : ViewModelBase
{
    public TransferSessionId SessionId { get; }

    [ObservableProperty]
    private string _rootBundleName;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _peersText = string.Empty;

    [ObservableProperty]
    private TransferSessionState _status;

    [ObservableProperty]
    private string _statusBadgeText = string.Empty;

    [ObservableProperty]
    private bool _isPendingConsent;

    [ObservableProperty]
    private bool _isInFlight;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isSeeding;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _isStalled;

    [ObservableProperty]
    private string? _failureFeedback;

    private readonly Func<TransferSessionId, Task>? _onAccept;
    private readonly Func<TransferSessionId, Task>? _onCancel;
    private readonly Func<TransferSessionId, Task>? _onPause;
    private readonly Func<TransferSessionId, Task>? _onResume;
    private readonly Func<TransferSessionId, Task>? _onStopSeeding;

    public FileTransferItemViewModel(
        TransferSessionId sessionId,
        string rootBundleName,
        long totalBytes,
        long downloadedBytes,
        TransferSessionState status,
        double speedBps = 0,
        int connectedPeers = 0,
        int connectedStreams = 0,
        Func<TransferSessionId, Task>? onAccept = null,
        Func<TransferSessionId, Task>? onCancel = null,
        Func<TransferSessionId, Task>? onPause = null,
        Func<TransferSessionId, Task>? onResume = null,
        Func<TransferSessionId, Task>? onStopSeeding = null)
    {
        SessionId = sessionId;
        _rootBundleName = rootBundleName;
        _totalBytes = totalBytes;
        _downloadedBytes = downloadedBytes;
        _status = status;
        _onAccept = onAccept;
        _onCancel = onCancel;
        _onPause = onPause;
        _onResume = onResume;
        _onStopSeeding = onStopSeeding;

        UpdateState(downloadedBytes, totalBytes, status, speedBps, connectedPeers, connectedStreams);
    }

    public void UpdateState(
        long downloadedBytes,
        long totalBytes,
        TransferSessionState status,
        double speedBps,
        int connectedPeers,
        int connectedStreams)
    {
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        Status = status;

        ProgressPercentage = totalBytes > 0
            ? Math.Clamp((double)downloadedBytes / totalBytes * 100.0, 0.0, 100.0)
            : 0.0;

        ProgressText = totalBytes > 0
            ? $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)} ({ProgressPercentage:F1}%)"
            : $"{FormatBytes(downloadedBytes)}";

        SpeedText = speedBps > 0 ? $"{FormatBytes((long)speedBps)}/s" : string.Empty;
        PeersText = connectedPeers > 0 ? $"{connectedPeers} peers ({connectedStreams} streams)" : "Direct P2P";

        StatusBadgeText = status switch
        {
            TransferSessionState.Offered => "Offered",
            TransferSessionState.PendingConsent => "Pending Consent",
            TransferSessionState.InFlight => "Downloading",
            TransferSessionState.Paused => "Paused",
            TransferSessionState.Completed => "Completed",
            TransferSessionState.Seeding => "Seeding",
            TransferSessionState.Stalled => "Stalled",
            TransferSessionState.Cancelled => "Cancelled",
            TransferSessionState.Failed => "Failed",
            _ => status.ToString()
        };

        IsPendingConsent = status == TransferSessionState.PendingConsent;
        IsInFlight = status == TransferSessionState.InFlight;
        IsPaused = status == TransferSessionState.Paused;
        IsSeeding = status == TransferSessionState.Seeding;
        IsCompleted = status == TransferSessionState.Completed;
        IsFailed = status == TransferSessionState.Failed;
        IsStalled = status == TransferSessionState.Stalled;

        if (IsFailed)
        {
            FailureFeedback = "Direct P2P connection failed. NAT traversal (STUN/ICE) could not establish a direct peer channel.";
        }
        else if (IsStalled)
        {
            FailureFeedback = "Transfer stalled: searching for active swarm peers. NAT traversal or seeder reachability may be restricted.";
        }
        else
        {
            FailureFeedback = null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    [RelayCommand]
    private Task AcceptAsync() => _onAccept?.Invoke(SessionId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CancelAsync() => _onCancel?.Invoke(SessionId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task PauseAsync() => _onPause?.Invoke(SessionId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ResumeAsync() => _onResume?.Invoke(SessionId) ?? Task.CompletedTask;

    [RelayCommand]
    private Task StopSeedingAsync() => _onStopSeeding?.Invoke(SessionId) ?? Task.CompletedTask;
}
