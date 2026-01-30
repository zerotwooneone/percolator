using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Simulator;

public sealed class JsonSimulatorStateStore : ISimulatorStateStore, IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly JsonSerializerOptions _json;
    private readonly string? _overridePath;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _pendingWriteCts;
    private SimulatorStateDto? _pendingState;
    private TaskCompletionSource? _pendingWriteTcs;

    public JsonSimulatorStateStore()
        : this(TimeSpan.FromMilliseconds(350), overridePath: null)
    {
    }

    public JsonSimulatorStateStore(TimeSpan debounce, string? overridePath = null)
    {
        _debounce = debounce;
        _overridePath = overridePath;
        _json = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public async Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetStatePath();
        if (!File.Exists(path)) return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<SimulatorStateDto>(fs, _json, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        CancellationToken token;
        TaskCompletionSource tcs;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingState = state;
            _pendingWriteCts?.Cancel();
            _pendingWriteCts?.Dispose();
            _pendingWriteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _pendingWriteCts.Token;

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prev = _pendingWriteTcs;
            _pendingWriteTcs = tcs;
            prev?.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
                await FlushLatestAsync(token).ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                // Debounced.
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        await tcs.Task.ConfigureAwait(false);
    }

    private async Task FlushLatestAsync(CancellationToken cancellationToken)
    {
        SimulatorStateDto? state;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state = _pendingState;
            _pendingState = null;
        }
        finally
        {
            _gate.Release();
        }

        if (state is null) return;

        var path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, state, _json, cancellationToken).ConfigureAwait(false);
        }

        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    private static string GetDefaultStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Percolator", "simulator-state.json");
    }

    private string GetStatePath()
    {
        return string.IsNullOrWhiteSpace(_overridePath) ? GetDefaultStatePath() : _overridePath;
    }

    public void Dispose()
    {
        _pendingWriteCts?.Cancel();
        _pendingWriteCts?.Dispose();
        _gate.Dispose();
    }
}
