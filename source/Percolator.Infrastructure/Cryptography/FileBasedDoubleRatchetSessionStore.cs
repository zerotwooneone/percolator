using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

public class FileBasedDoubleRatchetSessionStore : IDoubleRatchetSessionStore
{
    private readonly string _storagePath;
    private readonly ILogger<FileBasedDoubleRatchetSessionStore> _logger;

    public FileBasedDoubleRatchetSessionStore(
        IOptions<StorageOptions> storageOptions,
        ILogger<FileBasedDoubleRatchetSessionStore> logger)
    {
        _storagePath = Path.Combine(storageOptions.Value.Path, "sessions");
        _logger = logger;
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId)
    {
        var path = GetPath(sessionId.ToString());
        if (!File.Exists(path))
        {
            _logger.LogTrace("Session state file not found for session {SessionId}", sessionId);
            return null;
        }

        try
        {
            // Open the file for reading, but allow other processes to also read
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            var state = await JsonSerializer.DeserializeAsync(stream, CryptographyJsonContext.Default.DoubleRatchetSessionState);
            
            if (state != null)
            {
                // Log key hashes after deserialization
                var rootKeyHash = state.RootKey != null ? Convert.ToBase64String(SHA256.HashData(state.RootKey.Value)) : "null";
                _logger.LogTrace("Session {SessionId} deserialized - RootKey hash: {RootKeyHash}", 
                    sessionId, rootKeyHash);
            }
            
            return state;
        }
        catch (Exception ex)
        {
            // Log the exception for debugging
            _logger.LogError(ex, "Failed to deserialize session state for {SessionId}", sessionId);
            // If deserialization fails, return null as if the session doesn't exist
            return null;
        }
    }

    public async Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState)
    {
        // Log key hashes before serialization
        var rootKeyHash = sessionState.RootKey != null ? Convert.ToBase64String(SHA256.HashData(sessionState.RootKey.Value)) : "null";
        _logger.LogTrace("Storing session {SessionId} - RootKey hash: {RootKeyHash}", 
            sessionId, rootKeyHash);
        
        var path = GetPath(sessionId.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        
        // Open file for writing but allow other processes to read
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, sessionState, CryptographyJsonContext.Default.DoubleRatchetSessionState);
    }

    private string GetPath(string sessionId) =>
        Path.Combine(_storagePath, $"{sessionId}.json");
}
