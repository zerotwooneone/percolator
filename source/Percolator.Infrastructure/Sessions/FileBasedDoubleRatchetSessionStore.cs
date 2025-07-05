using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Application.Sessions;
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
            RootKey = state.RootKey,
            SendingChainKey = state.SendingChainKey,
            ReceivingChainKey = state.ReceivingChainKey,
            SendingCounter = state.SendingCounter,
            ReceivingCounter = state.ReceivingCounter,
            SkippedMessageKeys = state.SkippedMessageKeys,
            TheirIdentityPublicKey = state.TheirIdentityPublicKey,
            TheirDhRatchetPublicKey = state.TheirDhRatchetPublicKey,
            DhRatchetPrivateKey = state.DhRatchetPrivateKey
        };
    }

    private static DoubleRatchetSessionState ToDomain(SessionStateModel model)
    {
        return new DoubleRatchetSessionState
        {
            RootKey = model.RootKey,
            SendingChainKey = model.SendingChainKey,
            ReceivingChainKey = model.ReceivingChainKey,
            SendingCounter = model.SendingCounter,
            ReceivingCounter = model.ReceivingCounter,
            SkippedMessageKeys = model.SkippedMessageKeys,
            TheirIdentityPublicKey = model.TheirIdentityPublicKey,
            TheirDhRatchetPublicKey = model.TheirDhRatchetPublicKey,
            DhRatchetPrivateKey = model.DhRatchetPrivateKey
        };
    }

    private string GetPath(PeerId peerId, ConversationId conversationId) =>
        Path.Combine(_storageOptions.Path, "sessions", peerId.Value.ToString(), $"{conversationId.Value}.json");
}
