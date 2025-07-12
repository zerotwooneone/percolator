using Microsoft.Extensions.Options;
using Percolator.Sessions;

namespace Percolator.Infrastructure.Sessions;

public class FileBasedDoubleRatchetSessionStore: IDoubleRatchetSessionStore
{
    private readonly StorageOptions _storageOptions;

    public FileBasedDoubleRatchetSessionStore(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
    }

    public async Task SetSessionStateAsync(string sessionId, SessionState sessionState)
    {
        var path = GetPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, sessionState.Value);
    }

    public async Task<SessionState?> GetSessionStateAsync(string sessionId)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = await File.ReadAllBytesAsync(path);
        return new SessionState(value);
    }

    private string GetPath(string sessionId) =>
        Path.Combine(_storageOptions.Path, "sessions", $"{sessionId}.json");
}
