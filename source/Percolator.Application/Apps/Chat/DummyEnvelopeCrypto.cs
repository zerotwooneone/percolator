using Percolator.Chat.Primitives;

namespace Percolator.Application.Apps.Chat;

public class DummyEnvelopeCrypto : IEnvelopeCrypto
{
    public async Task<(byte[] chainKey, byte[] signingKey)> DecryptAsync(Guid conversationId, EncryptedGroupKey envelope, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}