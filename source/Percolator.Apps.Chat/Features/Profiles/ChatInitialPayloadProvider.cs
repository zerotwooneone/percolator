namespace Percolator.Apps.Chat.Features.Profiles;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

/// <summary>
/// Chat plugin contribution to outbound X3DH handshakes.
/// Generates an initial padded ProfileCapability 0-RTT payload carrying the sender's profile key,
/// blinded locator, sealed sender return route, and exchange relay coordinates.
/// </summary>
public sealed class ChatInitialPayloadProvider : IInitialHandshakePayloadProvider
{
    private readonly ILocalIdentityKeyStore _keyStore;
    private readonly IRelayDirectory _relayDirectory;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;

    public ChatInitialPayloadProvider(
        ILocalIdentityKeyStore keyStore,
        IRelayDirectory relayDirectory,
        ICryptoEngine cryptoEngine,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<(AppId AppId, ReadOnlyMemory<byte> Payload)?> CreateInitialPayloadAsync(
        PublicIdentityId localIdentityId,
        ChannelId channelId,
        CancellationToken ct = default)
    {
        var profileKey = await _keyStore.GetProfileKeyAsync(localIdentityId, ct).ConfigureAwait(false);
        if (profileKey == null)
        {
            return null;
        }

        using (profileKey)
        {
            var identityPublicKey = await _keyStore.GetIdentityPublicKeyAsync(localIdentityId, ct).ConfigureAwait(false);
            if (identityPublicKey == null)
            {
                return null;
            }

            // Initial 0-RTT capability advertises revision 1 as the baseline profile revision
            const uint revision = 1;

            var blindedLocator = BlindedProfileLocator.Derive(identityPublicKey, profileKey, _cryptoEngine);

            string? sourceEndpoint = null;
            byte[] mailboxRoutingTokenBytes = Array.Empty<byte>();
            byte[]? deliveryTokenBytes = null;

            try
            {
                var relays = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
                var linkedRelay = relays.FirstOrDefault(r => r.LinkedOwnerIdentityIds.Contains(localIdentityId));
                if (linkedRelay != null)
                {
                    sourceEndpoint = linkedRelay.Endpoint.Value;

                    var tokensResult = await _relayDirectory.GetMailboxTokensAsync(localIdentityId, linkedRelay.RelayIdentityId, ct).ConfigureAwait(false);
                    if (tokensResult.IsSuccess)
                    {
                        mailboxRoutingTokenBytes = tokensResult.Value.RoutingToken.ToByteArray();
                        deliveryTokenBytes = tokensResult.Value.DrainToken.Span.ToArray();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal if relay directory cannot be queried during initial payload creation
            }

            var dto = new ProfileCapabilityDto(
                channelId,
                localIdentityId,
                profileKey.Span.ToArray(),
                blindedLocator.Span.ToArray(),
                mailboxRoutingTokenBytes,
                revision,
                (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds(),
                identityPublicKey.Span.ToArray(),
                sourceEndpoint,
                ProfileSourceType.Relay,
                deliveryTokenBytes);

            var paddedBytes = SteppedPadding.CreatePaddedEnvelopeBytes(
                _serializer,
                ChatPayloadType.ProfileCapability,
                dto);

            return (AppId.Chat, paddedBytes);
        }
    }
}
