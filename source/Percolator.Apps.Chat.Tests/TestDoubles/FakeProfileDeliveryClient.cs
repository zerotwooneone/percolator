using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class FakeProfileDeliveryClient : IProfileDeliveryClient
{
    public Dictionary<BlindedProfileLocator, ProfileCiphertextPackage> HostedProfiles { get; } = new();
    public int FetchCallCount { get; private set; }

    public ValueTask<DomainResult<ProfileCiphertextPackage>> FetchProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        CancellationToken ct = default)
    {
        FetchCallCount++;
        if (HostedProfiles.TryGetValue(locator, out var package))
        {
            return ValueTask.FromResult(DomainResult<ProfileCiphertextPackage>.Success(package));
        }

        return ValueTask.FromResult(DomainResult<ProfileCiphertextPackage>.Failure(new DomainError("PROFILE_NOT_FOUND", "Profile slot not found.")));
    }

    public ValueTask<DomainResult> UploadProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default)
    {
        HostedProfiles[locator] = package;
        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<DomainResult> DeleteProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        CancellationToken ct = default)
    {
        HostedProfiles.Remove(locator);
        return ValueTask.FromResult(DomainResult.Success());
    }
}
