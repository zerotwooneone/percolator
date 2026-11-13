namespace Percolator.Infrastructure.Transport;



using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Channels;

using System.Threading.Tasks;

using Percolator.Domain.Common;

using Percolator.Infrastructure.Protos;

/// <summary>
/// Token-only mailbox capability store. The relay deliberately never persists an owner
/// identity — mailboxes, queued envelopes, and delivery authorization are addressed
/// purely by blinded routing token + bearer tokens.
/// </summary>
public interface IRelayMailboxCapabilityStore

{

    ValueTask RegisterAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> deliveryToken,

        ReadOnlyMemory<byte> drainToken,

        CancellationToken ct = default);



    ValueTask<DomainResult> RegisterVerifiedAsync(

        ReadOnlyMemory<byte> routingToken,

        ReadOnlyMemory<byte> deliveryToken,

        ReadOnlyMemory<byte> drainToken,

        ReadOnlyMemory<byte> existingDrainToken = default,

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
