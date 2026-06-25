using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Application.Chat.MessageQueue.Results;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat.MessageQueue;

public class MessageQueueService : IMessageQueueService
{
    public const int MaxBlobBytes = 10 * 1024; // 10 KB

    private readonly ILogger<MessageQueueService> _logger;
    private readonly IPeerPublicSigningKeyStore _publicKeyStore;
    private readonly IMessageQueueRepository _repository;

    public MessageQueueService(
        ILogger<MessageQueueService> logger,
        IPeerPublicSigningKeyStore publicKeyStore,
        IMessageQueueRepository repository)
    {
        _logger = logger;
        _publicKeyStore = publicKeyStore;
        _repository = repository;
    }

    public async Task<EnqueueOpaqueMessageResult> EnqueueOpaqueAsync(byte[] recipientPublicKeyHash, byte[] messageBlob, CancellationToken cancellationToken = default)
    {
        if (recipientPublicKeyHash is null || recipientPublicKeyHash.Length == 0)
        {
            return new EnqueueOpaqueMessageResult(false, "recipient_public_key_hash is required");
        }

        // Expect SHA-256 => 32 bytes. Be tolerant but log if size deviates.
        if (recipientPublicKeyHash.Length != 32)
        {
            _logger.LogWarning("Unexpected PKH length: {Length}. Expected 32.", recipientPublicKeyHash.Length);
        }

        if (messageBlob is null || messageBlob.Length == 0)
        {
            return new EnqueueOpaqueMessageResult(false, "message_blob is required");
        }

        if (messageBlob.Length > MaxBlobBytes)
        {
            return new EnqueueOpaqueMessageResult(false, $"message_blob exceeds {MaxBlobBytes} bytes");
        }

        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(recipientPublicKeyHash);
        var peerId = await _publicKeyStore.GetPeerIdByPublicKeyHashAsync(identityPublicKeyHash, cancellationToken).ConfigureAwait(false);
        if (peerId is null)
        {
            return new EnqueueOpaqueMessageResult(false, "unknown recipient_public_key_hash");
        }

        var queuedPayload = QueuedPayloadBytes.FromBytesOwned(messageBlob);
        (bool accepted, uint recipientCount, uint totalCount) = await _repository.TryEnqueueAsync(
            peerId,
            queuedPayload,
            cancellationToken).ConfigureAwait(false);

        if (!accepted)
        {
            return new EnqueueOpaqueMessageResult(false, "queue limits exceeded or rejected by policy");
        }

        return new EnqueueOpaqueMessageResult(true, null);
    }
}
