using System.Collections.Concurrent;
using Percolator.Application2IntegrationTests.Adapters;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2IntegrationTests.Builders;

public sealed class RelayNode
{
    private readonly ConcurrentDictionary<PublicIdentityId, (BlindedRoutingToken RoutingToken, DeliveryToken DeliveryToken)> _registeredClientTokens = new();

    public string Name { get; }
    public EndpointPort Port { get; }
    public PublicIdentityId RelayIdentityId { get; }
    public RelayMailboxQueue MailboxQueue { get; }
    public InMemoryRelayPreKeyRepository PreKeyRepository { get; }
    public InMemoryRelayProfileRepository ProfileRepository { get; } = new();
    public InMemoryRelayLedgerRepository LedgerRepository { get; } = new();
    public ScenarioZkProofEngine ZkEngine { get; } = new();
    public RelayHostingPolicy Policy { get; }
    public IDateTimeProvider TimeProvider { get; }

    public RelayNode(
        string name,
        EndpointPort port,
        RelayHostingPolicy? policy = null,
        IDateTimeProvider? timeProvider = null)
    {
        Name = name;
        Port = port;
        TimeProvider = timeProvider ?? new TestDateTimeProvider();
        Policy = policy ?? RelayHostingPolicy.Default;

        RelayIdentityId = PublicIdentityId.New();
        MailboxQueue = new RelayMailboxQueue(RelayIdentityId);
        PreKeyRepository = new InMemoryRelayPreKeyRepository();
    }

    public void RegisterClientMailbox(
        PublicIdentityId clientId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken)
    {
        MailboxQueue.RegisterRecipient(routingToken, deliveryToken, drainToken);
        _registeredClientTokens[clientId] = (routingToken, deliveryToken);
    }

    public bool TryGetClientTokens(
        PublicIdentityId clientId,
        out (BlindedRoutingToken RoutingToken, DeliveryToken DeliveryToken) tokens)
    {
        return _registeredClientTokens.TryGetValue(clientId, out tokens);
    }

    public void RegisterGroupLedger(RelayGroupLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        LedgerRepository.SaveAsync(ledger).GetAwaiter().GetResult();
    }

    public DomainResult DispatchGroupMessage(
        ChannelId channelId,
        ZkPresentationBytes proof,
        ReadOnlyMemory<byte> sealedPayload,
        IZkProofEngine? proofEngine = null)
    {
        var ledger = LedgerRepository.GetByChannelIdAsync(channelId).GetAwaiter().GetResult();
        if (ledger is null)
        {
            return DomainResult.Failure(new DomainError("LEDGER_NOT_FOUND", $"No group ledger found for channel {channelId}."));
        }

        var engine = proofEngine ?? ZkEngine;
        var verifyResult = ledger.VerifyDispatch(proof, sealedPayload.Span, engine);
        if (verifyResult.IsFailure)
        {
            return verifyResult;
        }

        var now = TimeProvider.UtcNow;
        var expiresAt = now.AddDays(7);

        foreach (var routingToken in ledger.ActiveRoutingTokens)
        {
            DeliveryToken? deliveryToken = null;
            foreach (var kvp in _registeredClientTokens)
            {
                if (kvp.Value.RoutingToken == routingToken)
                {
                    deliveryToken = kvp.Value.DeliveryToken;
                    break;
                }
            }

            if (deliveryToken is not null)
            {
                var envelope = new MailboxEnvelope(
                    EnvelopeId.New(),
                    routingToken,
                    sealedPayload.ToArray(),
                    now,
                    expiresAt);

                MailboxQueue.Enqueue(envelope, deliveryToken, TimeProvider);
            }
        }

        return DomainResult.Success();
    }

    public DomainResult<ProfileCiphertextPackage> GetProfile(BlindedProfileLocator locator)
    {
        var slot = ProfileRepository.GetSlotAsync(locator).GetAwaiter().GetResult();
        if (slot is null)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError("PROFILE_NOT_FOUND", "Profile slot not found."));
        }

        return slot.TryGet(TimeProvider);
    }

    public DomainResult UploadProfile(
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        var existing = ProfileRepository.GetSlotAsync(locator).GetAwaiter().GetResult();
        if (existing is null)
        {
            var createResult = RelayHostedProfileSlot.Create(locator, writeToken, package, revision, expiresAtUtc);
            if (createResult.IsFailure) return DomainResult.Failure(createResult.Error);
            ProfileRepository.SaveSlotAsync(createResult.Value!).GetAwaiter().GetResult();
            return DomainResult.Success();
        }

        var updateResult = existing.Update(writeToken, package, revision, expiresAtUtc);
        if (updateResult.IsFailure) return updateResult;
        ProfileRepository.SaveSlotAsync(existing).GetAwaiter().GetResult();
        return DomainResult.Success();
    }

    public DomainResult DeleteProfile(BlindedProfileLocator locator, ProfileWriteToken writeToken)
    {
        return ProfileRepository.DeleteSlotAsync(locator, writeToken).GetAwaiter().GetResult();
    }
}
