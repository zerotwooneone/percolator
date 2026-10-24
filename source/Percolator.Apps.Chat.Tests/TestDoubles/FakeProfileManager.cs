using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class FakeProfileManager : IProfileManager
{
    public Func<ProfileCiphertextPackage, ProfileKey, ICryptoEngine, uint?, DomainResult<ProfileMetadata>>? OnDecryptProfile { get; set; }

    public DomainResult<ProfileCiphertextPackage> EncryptProfile(
        ProfileMetadata metadata,
        ProfileKey profileKey,
        ICryptoEngine engine)
    {
        return DomainResult<ProfileCiphertextPackage>.Success(new ProfileCiphertextPackage(new byte[12], new byte[1040]));
    }

    public DomainResult<ProfileMetadata> DecryptProfile(
        ProfileCiphertextPackage package,
        ProfileKey profileKey,
        ICryptoEngine engine,
        uint? expectedRevision = null)
    {
        if (OnDecryptProfile is not null)
        {
            return OnDecryptProfile(package, profileKey, engine, expectedRevision);
        }

        return DomainResult<ProfileMetadata>.Success(new ProfileMetadata(
            "Fake Display Name",
            null,
            "Fake Bio",
            expectedRevision ?? 1));
    }
}
