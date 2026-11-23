namespace Percolator.Infrastructure.Transport;

using System;
using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Infrastructure.Protos;

public sealed class SealedEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
{
    private readonly ISessionWirePacker _wirePacker;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ILocalIdentityKeyStore _identityKeyStore;

    public SealedEnvelopeUnwrapper(
        ISessionWirePacker wirePacker,
        IDateTimeProvider timeProvider,
        ILocalIdentityKeyStore identityKeyStore)
    {
        _wirePacker = wirePacker ?? throw new ArgumentNullException(nameof(wirePacker));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
    }

    public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope, PublicIdentityId recipientId)
    {
        try
        {
            ReadOnlyMemory<byte> rawPayload = envelope.Ciphertext;

            if (rawPayload.Length > 0 && rawPayload.Span[0] != SealedWireFraming.SealedMagicByte)
            {
                try
                {
                    var proto = SealedEnvelopeProto.Parser.ParseFrom(rawPayload.Span);
                    if (proto != null && !proto.EncryptedPackage.IsEmpty)
                    {
                        rawPayload = proto.EncryptedPackage.Memory;
                    }
                }
                catch (InvalidProtocolBufferException)
                {
                    // Not a protobuf wrapper, proceed with raw bytes
                }
            }

            var privKey = _identityKeyStore.GetIdentityPrivateKeyAsync(recipientId).GetAwaiter().GetResult();
            var pubKey = _identityKeyStore.GetIdentityPublicKeyAsync(recipientId).GetAwaiter().GetResult();

            if (privKey == null || pubKey == null)
            {
                return DomainResult<InboundEnvelope>.Failure(
                    new DomainError("NO_IDENTITY_KEY", $"No identity keypair found for recipient {recipientId}."));
            }

            try
            {
                var decodeResult = SealedWireFraming.Decode(rawPayload, privKey.Span, pubKey);
                if (!decodeResult.IsSuccess)
                {
                    return DomainResult<InboundEnvelope>.Failure(decodeResult.Error!);
                }

                var (kind, channelId, senderId, senderDeviceId, senderIdentityKey, packedWireBytes) = decodeResult.Value;

                switch (kind)
                {
                    case WireFrameKind.DirectRatchet:
                    {
                        var unpackResult = _wirePacker.UnpackDirectRatchetMessage(
                            channelId, recipientId, senderId, senderDeviceId, packedWireBytes, envelope.EnqueuedAtUtc);
                        if (!unpackResult.IsSuccess)
                        {
                            return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                        }
                        return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
                    }
                    case WireFrameKind.GroupBroadcast:
                    {
                        var unpackResult = _wirePacker.UnpackGroupMessage(
                            channelId, recipientId, senderId, senderDeviceId, packedWireBytes, envelope.EnqueuedAtUtc);
                        if (!unpackResult.IsSuccess)
                        {
                            return DomainResult<InboundEnvelope>.Failure(unpackResult.Error!);
                        }
                        return DomainResult<InboundEnvelope>.Success(unpackResult.Value!);
                    }
                    case WireFrameKind.X3dhHandshake:
                    {
                        var unpackResult = _wirePacker.UnpackHandshakeMessage(
                            recipientId, senderId, senderDeviceId, packedWireBytes, envelope.EnqueuedAtUtc);
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
            finally
            {
                privKey.Dispose();
            }
        }
        catch (Exception ex)
        {
            return DomainResult<InboundEnvelope>.Failure(new DomainError("CORRUPTED_ENVELOPE", ex.Message));
        }
    }
}
