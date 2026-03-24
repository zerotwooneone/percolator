using System.IO;
using System.Text.Json;

namespace Desktop.Wpf.Features.Simulator;

public sealed class JsonSimulatorStateRepository : ISimulatorStateRepository, IDisposable
{
    private readonly JsonSerializerOptions _json;
    private readonly string? _overridePath;

    public JsonSimulatorStateRepository()
        : this(overridePath: null)
    {
    }

    public JsonSimulatorStateRepository(string? overridePath)
    {
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

    public async Task<RelayPersistenceDto?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default)
    {
        var path = GetRelayPath(relayHostPeerId);
        if (!File.Exists(path)) return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<RelayPersistenceDto>(fs, _json, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveRelayAsync(RelayPersistenceDto relay, CancellationToken cancellationToken = default)
    {
        if (relay is null) throw new ArgumentNullException(nameof(relay));

        var path = GetRelayPath(relay.RelayHostPeerId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, relay, _json, cancellationToken).ConfigureAwait(false);
        }

        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    private static string GetDefaultStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Percolator", "simulator-state.json");
    }

    private static string GetDefaultRelayDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Percolator", "simulator-relays");
    }

    private string GetStatePath()
    {
        return string.IsNullOrWhiteSpace(_overridePath) ? GetDefaultStatePath() : _overridePath;
    }

    private string GetRelayPath(Guid relayHostPeerId)
    {
        // Keep relays separate from simulator-state.json for greenfield persistence.
        var baseDir = string.IsNullOrWhiteSpace(_overridePath)
            ? GetDefaultRelayDirectory()
            : Path.Combine(Path.GetDirectoryName(_overridePath)!, "simulator-relays");

        return Path.Combine(baseDir, $"relay-{relayHostPeerId:N}.json");
    }

    public void Dispose()
    {
    }
}
