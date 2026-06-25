using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Chat;
using System.Security.Cryptography;
using ChatParticipantId = Percolator.Chat.Messaging.ValueObjects.ParticipantId;

namespace Percolator.Application.Identity;

/// <summary>
/// Holds the details of the currently active identity for the running node.
/// This context is populated at startup and treated as read-only thereafter.
/// </summary>
public class ActiveIdentityContext :ISelfParticipantIdProvider, IActiveIdentityMutator
{
    public IdentityRecord? Identity { get; internal set; }
    public X3dhKeys? Keys { get; internal set; }

    ChatParticipantId ISelfParticipantIdProvider.Get()
    {
        if (Identity is null)
        {
            throw new InvalidOperationException("Identity not loaded for chat participant.");
        }
        return new ChatParticipantId(Identity.PublicIdentityId.Value);
    }

    public void SetActiveIdentity(IdentityRecord identity, X3dhKeys? keys = null)
    {
        if (Keys is not null && keys is not null && !ReferenceEquals(Keys, keys))
        {
            Keys.Dispose();
        }

        Identity = identity;

        if (keys is null)
        {
            Keys = null;
            return;
        }

        var ikBytes = keys.IdentitySigningKey.ExportECPrivateKey();
        var spkBytes = keys.SignedPreKey.ExportECPrivateKey();

        var ikClone = ECDiffieHellman.Create();
        ikClone.ImportECPrivateKey(ikBytes, out _);

        var spkClone = ECDiffieHellman.Create();
        spkClone.ImportECPrivateKey(spkBytes, out _);

        Keys = new X3dhKeys(ikClone, spkClone);
    }
}

public interface IActiveIdentityMutator
{
    public void SetActiveIdentity(IdentityRecord identity, X3dhKeys keys);
}
