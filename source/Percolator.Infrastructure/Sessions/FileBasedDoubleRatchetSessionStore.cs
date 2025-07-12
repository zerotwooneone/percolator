using System.Text.Json;
using Microsoft.Extensions.Options;
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

    public async Task SetSessionStateAsync(string sessionId, DoubleRatchetSessionState sessionState)
    {
        var model = ToModel(sessionState);
        var path = GetPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(model, _jsonContext.SessionStateModel);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<DoubleRatchetSessionState?> GetSessionStateAsync(string sessionId)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        var model = JsonSerializer.Deserialize(json, _jsonContext.SessionStateModel);

        return model is null ? null : ToDomain(model);
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
            TheirIdentityPublicKey = new RatchetIdentityKey(Convert.FromBase64String(model.TheirIdentityPublicKey!)),
            TheirDhRatchetPublicKey = model.TheirDhRatchetPublicKey is not null ? new RatchetEphemeralKey(Convert.FromBase64String(model.TheirDhRatchetPublicKey)) : null,
            DhRatchetPrivateKey = model.DhRatchetPrivateKey is not null ? new PrivateEphemeralKey(Convert.FromBase64String(model.DhRatchetPrivateKey)) : null
        };
    }

    private string GetPath(string sessionId) =>
        Path.Combine(_storageOptions.Path, "sessions", $"{sessionId}.json");
}
