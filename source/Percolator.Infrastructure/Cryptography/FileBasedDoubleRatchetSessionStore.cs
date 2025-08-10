using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Infrastructure.Cryptography;

public class FileBasedDoubleRatchetSessionStore : IDoubleRatchetSessionStore
{
    private readonly ILogger<FileBasedDoubleRatchetSessionStore> _logger;
    private readonly ISelfIdentityProvider _selfIdentityProvider;
    private readonly CryptographyOptions _options;
    private readonly IOptions<StorageOptions> _storageOptions;

    public FileBasedDoubleRatchetSessionStore(
        IOptions<StorageOptions> storageOptions,
        ILogger<FileBasedDoubleRatchetSessionStore> logger,
        IOptions<CryptographyOptions> options,
        ISelfIdentityProvider selfIdentityProvider)
    {
        _logger = logger;
        _selfIdentityProvider = selfIdentityProvider;
        _options = options.Value;
        _storageOptions = storageOptions;
    }

    public async Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId)
    {
        var path = GetPath(sessionId.ToString(), _selfIdentityProvider.Get().Value.ToString());
        if (!File.Exists(path))
        {
            _logger.LogWarning("Session state file not found for session {SessionId}", sessionId);
            return null;
        }
        _logger.LogInformation("Reading Session state file for session {SessionId} at {Path}", sessionId, path);

        try
        {
            // Open the file for reading, but allow other processes to also read
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            var state = await JsonSerializer.DeserializeAsync(stream, CryptographyJsonContext.Default.DoubleRatchetSessionState);
            
            if (state != null)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    var rootKeyHash = state.RootKey != null ? Convert.ToBase64String(state.RootKey.Value) : "null";
                    _logger.LogTrace("Session {SessionId} deserialized - RootKey hash: {RootKeyHash}", 
                        sessionId, rootKeyHash);
                }

                if (_options.EnableCryptographicMaterialLogging)
                {
                    _logger.LogInformation("Session {SessionId} for {Path} deserialized - RootKey: {RootKey} {DhRatchetPrivateKey}",sessionId, path,state.RootKey, state.DhRatchetPrivateKey);
                }
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
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            var rootKey = sessionState.RootKey != null ? Convert.ToBase64String(sessionState.RootKey.Value) : "null";
            _logger.LogTrace("Storing session {SessionId} - RootKey : {RootKey}", 
                sessionId, rootKey);
        }

        if (_options.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Storing session {SessionId} - RootKey: {RootKey} {DhRatchetPrivateKey}", sessionId, sessionState.RootKey, sessionState.DhRatchetPrivateKey);
        }
        
        
        var path = GetPath(sessionId.ToString(), _selfIdentityProvider.Get().Value.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _logger.LogInformation("Writing Session state file for session {SessionId} at {Path}", sessionId, path);
        // Open file for writing but allow other processes to read
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, sessionState, CryptographyJsonContext.Default.DoubleRatchetSessionState);
    }

    private string GetPath(string sessionId, string peerId)
    {
        var path = Path.Combine(_storageOptions.Value.Path, "sessions", peerId);
        Directory.CreateDirectory(path);
        return Path.Combine(path, $"{sessionId}.json");
    }
}