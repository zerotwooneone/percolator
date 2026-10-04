using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Services;

public sealed class GroupKeyDistributionService : IGroupKeyDistributionService
{
    private const byte MessageTypeSenderKeyDistribution = 0x01;
    public const int DistributionPayloadLength = 1 + 16 + 4 + 4 + 32 + 32; // 89 bytes

    private readonly IGroupSenderKeyRepository _senderKeyRepo;
    private readonly IGroupReceiverSessionRepository _receiverSessionRepo;
    private readonly IPayloadSender _payloadSender;
    private readonly ICryptoEngine _cryptoEngine;

    public GroupKeyDistributionService(
        IGroupSenderKeyRepository senderKeyRepo,
        IGroupReceiverSessionRepository receiverSessionRepo,
        IPayloadSender payloadSender,
        ICryptoEngine cryptoEngine)
    {
        _senderKeyRepo = senderKeyRepo ?? throw new ArgumentNullException(nameof(senderKeyRepo));
        _receiverSessionRepo = receiverSessionRepo ?? throw new ArgumentNullException(nameof(receiverSessionRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
    }

    public async ValueTask<DomainResult> DistributeSenderKeyAsync(
        ChannelId channelId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        IEnumerable<PublicIdentityId> recipientMemberIds,
        CancellationToken ct = default)
    {
        var ratchet = await _senderKeyRepo.GetSenderKeyRatchetAsync(channelId, senderId, senderDeviceId, ct);
        if (ratchet == null)
        {
            var initialChainBytes = new byte[32];
            RandomNumberGenerator.Fill(initialChainBytes);
            using var initialChain = ChainKey.FromSpan(initialChainBytes);
            var (signingPriv, signingPub) = _cryptoEngine.GenerateEphemeralKeyPair();
            var signingKey = IdentityKey.FromSpan(signingPub.Span);

            ratchet = new GroupSenderKeyRatchet(
                channelId,
                senderId,
                senderDeviceId,
                initialChain,
                initialIteration: 0,
                keyId: 1,
                signingPrivateKey: signingPriv,
                authorSigningPublicKey: signingKey);

            await _senderKeyRepo.SaveSenderKeyRatchetAsync(ratchet, ct);
        }

        if (ratchet.CurrentChainKey == null || ratchet.AuthorSigningPublicKey == null)
        {
            return DomainResult.Failure(new DomainError(
                "SENDER_KEY_CORRUPT", "Sender key ratchet has no valid chain key or author signing key."));
        }

        byte[] payload = new byte[DistributionPayloadLength];
        payload[0] = MessageTypeSenderKeyDistribution;
        channelId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17, 4), ratchet.KeyId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(21, 4), ratchet.Iteration);
        ratchet.CurrentChainKey.Span.CopyTo(payload.AsSpan(25, 32));
        ratchet.AuthorSigningPublicKey.Span.CopyTo(payload.AsSpan(57, 32));

        foreach (var recipient in recipientMemberIds)
        {
            if (recipient == senderId)
            {
                continue; // Do not distribute to self
            }

            var outboundContext = new OutboundPayloadContext(
                channelId,
                senderId,
                recipient,
                DeviceId.Primary,
                AppId.SystemControl,
                payload,
                DeliveryRoute.DirectP2P);

            var sendResult = await _payloadSender.SendPayloadAsync(outboundContext, ct);
            if (!sendResult.IsSuccess)
            {
                return sendResult;
            }
        }

        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> ProcessInboundDistributionAsync(
        InboundPayloadContext context,
        CancellationToken ct = default)
    {
        if (context.Payload.Length < DistributionPayloadLength)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_PAYLOAD_SIZE", $"Sender key distribution message must be at least {DistributionPayloadLength} bytes."));
        }

        var span = context.Payload.Span;
        if (span[0] != MessageTypeSenderKeyDistribution)
        {
            return DomainResult.Failure(new DomainError(
                "UNSUPPORTED_SYSTEM_MESSAGE", $"System message type 0x{span[0]:X2} is not recognized."));
        }

        var channelId = ChannelId.FromBytes(span.Slice(1, 16));
        uint keyId = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(17, 4));
        uint iteration = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(21, 4));
        var chainKeyBytes = span.Slice(25, 32);
        var signingKeyBytes = span.Slice(57, 32);

        using var chainKey = ChainKey.FromSpan(chainKeyBytes);
        var signingKey = IdentityKey.FromSpan(signingKeyBytes);

        var receiverSession = new GroupReceiverSession(
            channelId,
            context.SenderIdentityId,
            context.SenderDeviceId,
            chainKey,
            initialIteration: iteration,
            keyId: keyId,
            authorSigningKey: signingKey);

        await _receiverSessionRepo.SaveReceiverSessionAsync(receiverSession, ct);
        return DomainResult.Success();
    }
}
