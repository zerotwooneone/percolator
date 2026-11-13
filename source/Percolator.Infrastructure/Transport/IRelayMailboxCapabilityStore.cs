namespace Percolator.Infrastructure.Transport;



using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Channels;

using System.Threading.Tasks;

using Percolator.Domain.Common;

using Percolator.Domain.Identities.ValueObjects;

using Percolator.Infrastructure.Protos;



public interface IRelayMailboxCapabilityStore

{

    ValueTask RegisterAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> deliveryToken,

        ReadOnlyMemory<byte> drainToken,

        PublicIdentityId? ownerId = null,

        DeviceId? deviceId = null,

        CancellationToken ct = default);



    ValueTask<DomainResult> RegisterVerifiedAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> deliveryToken,

        ReadOnlyMemory<byte> drainToken,

        ReadOnlyMemory<byte> existingDrainToken = default,

        PublicIdentityId? ownerId = null,

        DeviceId? deviceId = null,

        CancellationToken ct = default);



    ValueTask AssociateOwnerAsync(

        ReadOnlyMemory<byte> routingToken,

        PublicIdentityId ownerId,

        DeviceId deviceId,

        CancellationToken ct = default);



    ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(

        ReadOnlyMemory<byte> routingToken,

        CancellationToken ct = default);



    ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(

        PublicIdentityId ownerId,

        CancellationToken ct = default);



    ValueTask<bool> VerifyDeliveryTokenAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> deliveryToken,

        CancellationToken ct = default);



    ValueTask<bool> VerifyDrainTokenAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> drainToken,

        CancellationToken ct = default);



    ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(

        ReadOnlyMemory<byte> routingToken,

        CancellationToken ct = default);



    ValueTask EnqueueEnvelopeAsync(

        ReadOnlyMemory<byte> routingToken,

        QueuedSealedEnvelopeProto envelope,

        CancellationToken ct = default);



    ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(

        ReadOnlyMemory<byte> routingToken,

        CancellationToken ct = default);



    ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(

        ReadOnlyMemory<byte> routingToken,

        CancellationToken ct = default);



    IDisposable SubscribeMailbox(

        ReadOnlyMemory<byte> routingToken,

        ChannelWriter<QueuedSealedEnvelopeProto> writer);



    ValueTask<bool> AcknowledgeEnvelopeAsync(

        ReadOnlyMemory<byte> routingToken,

        string queueId,

        CancellationToken ct = default);

}