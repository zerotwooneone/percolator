using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Simulator.Features.Logging;

internal sealed class MultiSinkLogEngine : ISimulatorLogSink
{
    private const string LogDirectoryName = "logs";
    private const string LatestLogFileName = "simulator-latest.jsonl";
    private const int MaxArchivedLogs = 10;

    private readonly Channel<SimulatorLogEntry> _channel;
    private readonly Subject<SimulatorLogEntry> _subject = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private readonly string _logFilePath;

    public Observable<SimulatorLogEntry> LogStream => _subject;

    public MultiSinkLogEngine(string? baseDirectory = null)
    {
        _channel = Channel.CreateUnbounded<SimulatorLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        var root = baseDirectory ?? AppContext.BaseDirectory;
        var logDir = Path.Combine(root, LogDirectoryName);
        Directory.CreateDirectory(logDir);

        _logFilePath = Path.Combine(logDir, LatestLogFileName);
        RotateExistingLatestLog(logDir);

        _consumerTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));
    }

    private void RotateExistingLatestLog(string logDir)
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                var timestamp = File.GetLastWriteTimeUtc(_logFilePath).ToString("yyyyMMdd-HHmmss");
                var archivePath = Path.Combine(logDir, $"simulator-{timestamp}.jsonl");
                File.Move(_logFilePath, archivePath, overwrite: true);
            }

            // Prune to 10 most recent archived files
            var di = new DirectoryInfo(logDir);
            var archivedFiles = di.GetFiles("simulator-*.jsonl")
                .Where(f => !f.Name.Equals(LatestLogFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxArchivedLogs)
                .ToList();

            foreach (var file in archivedFiles)
            {
                try { file.Delete(); } catch { /* best effort cleanup */ }
            }
        }
        catch
        {
            // Ignore rotation errors on startup
        }
    }

    public void Log(SimulatorLogEntry entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessLogQueueAsync(CancellationToken ct)
    {
        try
        {
            using var fileStream = new FileStream(
                _logFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            using var writer = new StreamWriter(fileStream) { AutoFlush = true };

            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    // 1. Publish to in-memory reactive stream for UI consumption
                    _subject.OnNext(entry);

                    // 2. Format JSONL line for rolling disk file
                    var jsonLine = JsonSerializer.Serialize(entry);
                    await writer.WriteLineAsync(jsonLine.AsMemory(), ct);

                    // 3. Format line for stdout (AI / terminal observability)
                    var nodePrefix = string.IsNullOrEmpty(entry.NodeName) ? "" : $"[{entry.NodeName}] ";
                    Console.WriteLine($"[{entry.TimestampUtc:HH:mm:ss.fff}] [{entry.Level}] {nodePrefix}{entry.Category}: {entry.Message}");
                    if (!string.IsNullOrEmpty(entry.Exception))
                    {
                        Console.WriteLine($"  Exception: {entry.Exception}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MultiSinkLogEngine] Consumer loop error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _consumerTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore cancelation on dispose
        }

        _subject.OnCompleted();
        _subject.Dispose();
        _cts.Dispose();
    }
}
