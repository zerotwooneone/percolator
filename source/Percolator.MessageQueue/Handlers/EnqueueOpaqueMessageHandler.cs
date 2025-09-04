using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;

namespace Percolator.MessageQueue.Handlers;

public class EnqueueOpaqueMessageHandler : IRequestHandler<EnqueueOpaqueMessageCommand, EnqueueOpaqueMessageResult>
{
    public const int MaxBlobBytes = 10 * 1024; // 10 KB

    private readonly ILogger<EnqueueOpaqueMessageHandler> _logger;
    private readonly IPeerPublicSigningKeyStore _publicKeyStore;
    private readonly IMessageQueueRepository _repository;

    public EnqueueOpaqueMessageHandler(
        ILogger<EnqueueOpaqueMessageHandler> logger,
        IPeerPublicSigningKeyStore publicKeyStore,
        IMessageQueueRepository repository)
    {
        _logger = logger;
        _publicKeyStore = publicKeyStore;
        _repository = repository;
    }

    public async Task<EnqueueOpaqueMessageResult> Handle(EnqueueOpaqueMessageCommand request, CancellationToken cancellationToken)
    {
        //todo: throw instead of returning false
        if (request.RecipientPublicKeyHash is null || request.RecipientPublicKeyHash.Length == 0)
        {
            return new EnqueueOpaqueMessageResult(false, "recipient_public_key_hash is required");
        }

        // Expect SHA-256 => 32 bytes. Be tolerant but log if size deviates.
        if (request.RecipientPublicKeyHash.Length != 32)
        {
            _logger.LogWarning("Unexpected PKH length: {Length}. Expected 32.", request.RecipientPublicKeyHash.Length);
        }

        if (request.MessageBlob is null || request.MessageBlob.Length == 0)
        {
            return new EnqueueOpaqueMessageResult(false, "message_blob is required");
        }

        if (request.MessageBlob.Length > MaxBlobBytes)
        {
            return new EnqueueOpaqueMessageResult(false, $"message_blob exceeds {MaxBlobBytes} bytes");
        }

        var peerId = await _publicKeyStore.GetPeerIdByPublicKeyHashAsync(request.RecipientPublicKeyHash, cancellationToken);
        if (peerId is null)
        {
            return new EnqueueOpaqueMessageResult(false, "unknown recipient_public_key_hash");
        }

        (bool accepted, uint recipientCount, uint totalCount) = await _repository.TryEnqueueAsync(
            peerId,
            request.MessageBlob,
            cancellationToken);

        if (!accepted)
        {
            return new EnqueueOpaqueMessageResult(false, "queue limits exceeded or rejected by policy");
        }

        return new EnqueueOpaqueMessageResult(true, null);
    }
}
