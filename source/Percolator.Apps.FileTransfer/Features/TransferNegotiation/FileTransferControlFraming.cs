using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public static class FileTransferControlFraming
{
    public static byte[] FramePayload<TPayload>(
        IPayloadSerializer serializer,
        FileTransferControlOpcode opcode,
        TPayload payload)
    {
        ReadOnlyMemory<byte> serialized = serializer.Serialize(payload);
        byte[] framed = new byte[1 + serialized.Length];
        framed[0] = (byte)opcode;
        serialized.Span.CopyTo(framed.AsSpan(1));
        return framed;
    }

    public static OutboundPayloadContext BuildOutboundContext(
        ChannelId channelId,
        PublicIdentityId localIdentityId,
        PublicIdentityId? directRecipientId,
        bool isGroupTransfer,
        byte[] framedPayload)
    {
        return isGroupTransfer
            ? new OutboundPayloadContext(
                channelId,
                localIdentityId,
                RecipientIdentityId: null,
                DeviceId.Primary,
                AppId.FileTransferControl,
                framedPayload,
                DeliveryRoute.RelayedGroup)
            : new OutboundPayloadContext(
                channelId,
                localIdentityId,
                directRecipientId ?? throw new ArgumentNullException(nameof(directRecipientId)),
                DeviceId.Primary,
                AppId.FileTransferControl,
                framedPayload,
                DeliveryRoute.RelayedOneToOne);
    }
}
