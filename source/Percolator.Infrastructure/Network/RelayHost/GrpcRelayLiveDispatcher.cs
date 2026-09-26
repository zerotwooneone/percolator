using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Percolator.Chat.GroupLedger;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.Infrastructure.Network.RelayHost;

public sealed class GrpcRelayLiveDispatcher : IRelayLiveDispatcher
{
    private readonly ConcurrentDictionary<PublicIdentityId, Channel<ServerRelayStream>> _channels;
    private readonly ILogger<GrpcRelayLiveDispatcher> _logger;

    public GrpcRelayLiveDispatcher(ILogger<GrpcRelayLiveDispatcher> logger)
    {
        _channels = new ConcurrentDictionary<PublicIdentityId, Channel<ServerRelayStream>>();
        _logger = logger;
    }

    public void RegisterStream(PublicIdentityId publicIdentityId, Channel<ServerRelayStream> channel)
    {
        _channels.TryAdd(publicIdentityId, channel);
        _logger.LogDebug("Registered stream for {PublicIdentityId}", publicIdentityId);
    }

    public void UnregisterStream(PublicIdentityId publicIdentityId)
    {
        _channels.TryRemove(publicIdentityId, out _);
        _logger.LogDebug("Unregistered stream for {PublicIdentityId}", publicIdentityId);
    }

    public async Task PushGroupMessageAsync(
        ConversationId conversationId,
        uint senderKeyId,
        CiphertextBytes ciphertext,
        IReadOnlyList<PublicIdentityId> targetIdentities,
        CancellationToken ct)
    {
        var envelope = new ServerRelayStream
        {
            GroupMessage = new GroupMessageEnvelope
            {
                ConversationId = ByteString.CopyFrom(conversationId.Value.ToByteArray()),
                SenderKeyId = senderKeyId,
                Ciphertext = ByteString.CopyFrom(ciphertext.ToArray()),
                AckId = undefined_ack_id.ToByteArray()
            }
        };

        foreach (var targetIdentity in targetIdentities)
        {
            if (_channels.TryGetValue(targetIdentity, out var channel))
            {
                try
                {
                    await channel.Writer.WriteAsync(envelope, ct);
                    _logger.LogDebug("Pushed group message to {PublicIdentityId}", targetIdentity);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to push group message to {PublicIdentityId}", targetIdentity);
                }
            }
        }
    }

    public async Task PushOpaqueMessageAsync(
        PublicIdentityId target,
        Guid ackId,
        byte[] payload,
        CancellationToken ct)
    {
        var delivery = new ServerRelayStream
        {
            OpaqueDelivery = new OpaqueMessageDelivery
            {
                AckId = ByteString.CopyFrom(ackId.ToByteArray()),
                OpaquePayload = ByteString.CopyFrom(payload)
            }
        };

        if (_channels.TryGetValue(target, out var channel))
        {
            try
            {
                await channel.Writer.WriteAsync(delivery, ct);
                _logger.LogDebug("Pushed opaque message to {PublicIdentityId}", target);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push opaque message to {PublicIdentityId}", target);
            }
        }
    }
}
