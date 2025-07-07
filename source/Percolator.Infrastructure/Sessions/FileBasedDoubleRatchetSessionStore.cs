using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Infrastructure.Serialization;
using Percolator.Sessions;
using DoubleRatchetSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.Infrastructure.Sessions;

public class FileBasedDoubleRatchetSessionStore: IDoubleRatchetSessionStore
{
    private readonly StorageOptions _storageOptions;
    private readonly PercolatorJsonContext _jsonContext;

    public FileBasedDoubleRatchetSessionStore(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public async Task SaveSessionStateAsync(PeerId peerId, ConversationId conversationId, DoubleRatchetSessionState sessionState)
    {
        var model = ToModel(sessionState);
        var path = GetPath(peerId, conversationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(model, _jsonContext.SessionStateModel);
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
        var model = JsonSerializer.Deserialize(json, _jsonContext.SessionStateModel);

        return model is null ? null : ToDomain(model);
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

    private static SessionStateModel ToModel(DoubleRatchetSessionState state)
    {
        return new SessionStateModel
        {
            RootKey = state.RootKey.Value,
            SendingChainKey = state.SendingChainKey?.Value,
            ReceivingChainKey = state.ReceivingChainKey?.Value,
            SendingCounter = state.SendingCounter,
            ReceivingCounter = state.ReceivingCounter,
            SkippedMessageKeys = state.SkippedMessageKeys.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value),
            TheirIdentityPublicKey = state.TheirIdentityPublicKey.Value,
            TheirDhRatchetPublicKey = state.TheirDhRatchetPublicKey?.Value,
            DhRatchetPrivateKey = state.DhRatchetPrivateKey is not null ? state.DhRatchetPrivateKey.Value : null
        };
    }

    private static DoubleRatchetSessionState ToDomain(SessionStateModel model)
    {
        return new DoubleRatchetSessionState
        {
            RootKey = new RootKey(model.RootKey!),
            SendingChainKey = model.SendingChainKey is not null ? new ChainKey(model.SendingChainKey) : null,
            ReceivingChainKey = model.ReceivingChainKey is not null ? new ChainKey(model.ReceivingChainKey) : null,
            SendingCounter = model.SendingCounter,
            ReceivingCounter = model.ReceivingCounter,
            SkippedMessageKeys = model.SkippedMessageKeys.ToDictionary(kvp => kvp.Key, kvp => new MessageKey(kvp.Value)),
            TheirIdentityPublicKey = new PublicKey(model.TheirIdentityPublicKey!),
            TheirDhRatchetPublicKey = model.TheirDhRatchetPublicKey is not null ? new PublicKey(model.TheirDhRatchetPublicKey) : null,
            DhRatchetPrivateKey = model.DhRatchetPrivateKey is not null ? new PrivateKey(model.DhRatchetPrivateKey) : null
        };
    }

    private string GetPath(PeerId peerId, ConversationId conversationId) =>
        Path.Combine(_storageOptions.Path, "sessions", peerId.Value.ToString(), $"{conversationId.Value}.json");
}
