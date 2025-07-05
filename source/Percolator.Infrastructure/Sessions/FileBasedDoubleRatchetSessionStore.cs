using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Application.Sessions;
using Percolator.Sessions;
using DoubleRatchetSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.Infrastructure.Sessions;

public class FileBasedDoubleRatchetSessionStore: IDoubleRatchetSessionStore
{
    private readonly StorageOptions _storageOptions;

    public FileBasedDoubleRatchetSessionStore(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
    }

    public async Task SaveSessionStateAsync(PeerId peerId, ConversationId conversationId, DoubleRatchetSessionState sessionState)
    {
        var path = GetPath(peerId, conversationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(sessionState);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<DoubleRatchetSessionState?> GetSessionStateAsync(PeerId peerId, ConversationId conversationId)
    {
        var path = GetPath(peerId, conversationId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<DoubleRatchetSessionState>(json);
    }

    public Task DeleteSessionStateAsync(PeerId peerId, ConversationId conversationId)
    {
        var path = GetPath(peerId, conversationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(PeerId peerId, ConversationId conversationId) =>
        Path.Combine(_storageOptions.Path, "sessions", peerId.Value.ToString(), $"{conversationId.Value}.json");
}
