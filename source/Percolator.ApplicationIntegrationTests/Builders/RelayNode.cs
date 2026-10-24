using System.Collections.Concurrent;
using Percolator.Application2IntegrationTests.Adapters;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
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
