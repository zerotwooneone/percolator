using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class SealedEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
{
    private readonly ISessionWirePacker _wirePacker;
    private readonly PublicIdentityId _recipientId;
    private readonly IDateTimeProvider _timeProvider;

    public SealedEnvelopeUnwrapper(
        ISessionWirePacker wirePacker,
        PublicIdentityId recipientId,
        IDateTimeProvider timeProvider)
    {
        _wirePacker = wirePacker ?? throw new ArgumentNullException(nameof(wirePacker));
        _recipientId = recipientId;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope)
    {
        return UnwrapFrame(envelope.Ciphertext, _recipientId, _timeProvider.UtcNow);
    }

    public DomainResult<InboundEnvelope> UnwrapFrame(
        ReadOnlyMemory<byte> frameBytes,
        PublicIdentityId recipientId,
        DateTimeOffset receivedAtUtc)
    {
        var decodeResult = SealedWireFraming.Decode(frameBytes);
        if (!decodeResult.IsSuccess)
        {
            return DomainResult<InboundEnvelope>.Failure(decodeResult.Error!);
        }

        var (kind, channelId, senderId, senderDeviceId, packedWireBytes) = decodeResult.Value;

        switch (kind)
        {
            case WireFrameKind.DirectRatchet:
            {
                var unpackResult = _wirePacker.UnpackDirectRatchetMessage(
                    channelId, recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                if (!unpackResult.IsSuccess)
                {
                    return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                }
                return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
            }
            case WireFrameKind.GroupBroadcast:
            {
                var unpackResult = _wirePacker.UnpackGroupMessage(
                    channelId, recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                if (!unpackResult.IsSuccess)
                {
                    return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                }
                return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
            }
            case WireFrameKind.X3dhHandshake:
            {
                var unpackResult = _wirePacker.UnpackHandshakeMessage(
                    recipientId, senderId, senderDeviceId, packedWireBytes, receivedAtUtc);
                if (!unpackResult.IsSuccess)
                {
                    return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                }
                return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
            }
            default:
                return DomainResult<InboundEnvelope>.Failure(new DomainError("UNKNOWN_KIND", $"Unknown WireFrameKind: {kind}"));
        }
    }
}
