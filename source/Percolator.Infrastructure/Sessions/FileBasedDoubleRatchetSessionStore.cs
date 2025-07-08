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
            RootKey = Convert.ToBase64String(state.RootKey.Value),
            SendingChainKey = state.SendingChainKey is not null ? Convert.ToBase64String(state.SendingChainKey.Value) : null,
            ReceivingChainKey = state.ReceivingChainKey is not null ? Convert.ToBase64String(state.ReceivingChainKey.Value) : null,
            SendingCounter = state.SendingCounter,
            ReceivingCounter = state.ReceivingCounter,
            SkippedMessageKeys = state.SkippedMessageKeys.ToDictionary(kvp => kvp.Key.ToString(), kvp => Convert.ToBase64String(kvp.Value.Value)),
            TheirIdentityPublicKey = Convert.ToBase64String(state.TheirIdentityPublicKey.Value),
            TheirDhRatchetPublicKey = state.TheirDhRatchetPublicKey is not null ? Convert.ToBase64String(state.TheirDhRatchetPublicKey.Value) : null,
            DhRatchetPrivateKey = state.DhRatchetPrivateKey is not null ? Convert.ToBase64String(state.DhRatchetPrivateKey.Value) : null
        };
    }

    private static DoubleRatchetSessionState ToDomain(SessionStateModel model)
    {
        return new DoubleRatchetSessionState
        {
            RootKey = new RootKey(Convert.FromBase64String(model.RootKey!)),
            SendingChainKey = model.SendingChainKey is not null ? new ChainKey(Convert.FromBase64String(model.SendingChainKey)) : null,
            ReceivingChainKey = model.ReceivingChainKey is not null ? new ChainKey(Convert.FromBase64String(model.ReceivingChainKey)) : null,
            SendingCounter = model.SendingCounter,
            ReceivingCounter = model.ReceivingCounter,
            SkippedMessageKeys = model.SkippedMessageKeys.ToDictionary(kvp => ulong.Parse(kvp.Key), kvp => new MessageKey(Convert.FromBase64String(kvp.Value))),
            TheirIdentityPublicKey = new PublicKey(Convert.FromBase64String(model.TheirIdentityPublicKey!)),
            TheirDhRatchetPublicKey = model.TheirDhRatchetPublicKey is not null ? new PublicKey(Convert.FromBase64String(model.TheirDhRatchetPublicKey)) : null,
            DhRatchetPrivateKey = model.DhRatchetPrivateKey is not null ? new PrivateKey(Convert.FromBase64String(model.DhRatchetPrivateKey)) : null
        };
    }

    private string GetPath(PeerId peerId, ConversationId conversationId) =>
        Path.Combine(_storageOptions.Path, "sessions", peerId.Value.ToString(), $"{conversationId.Value}.json");
}
