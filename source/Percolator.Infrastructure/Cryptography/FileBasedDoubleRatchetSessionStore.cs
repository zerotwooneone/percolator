using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

public class FileBasedDoubleRatchetSessionStore : IDoubleRatchetSessionStore
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public FileBasedDoubleRatchetSessionStore(IOptions<StorageOptions> storageOptions)
    {
        _storagePath = Path.Combine(storageOptions.Value.Path, "sessions");
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId)
    {
        var path = GetPath(sessionId.ToString());
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Open the file for reading, but allow other processes to also read
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return JsonSerializer.Deserialize<DoubleRatchetSession.DoubleRatchetSessionState>(json, _jsonOptions);
        }
        catch
        {
            // If deserialization fails, return null as if the session doesn't exist
            return null;
        }
    }

    public async Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState)
    {
        var path = GetPath(sessionId.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        
        var json = JsonSerializer.Serialize(sessionState, _jsonOptions);
        
        // Open file for writing but allow other processes to read
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
    }

    private string GetPath(string sessionId) =>
        Path.Combine(_storagePath, $"{sessionId}.json");
}
