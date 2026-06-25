using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class SqliteSelfIdentityDomainRepository : ISelfIdentityRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteSelfIdentityDomainRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<SelfIdentity?> GetMostRecentAsync(CancellationToken ct = default)
    {
        var dbo = await _db.SelfIdentities
            .AsNoTracking()
            .OrderByDescending(x => x.LastUsedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return dbo is null ? null : Map(dbo);
    }

    public async Task<SelfIdentity?> GetByIdAsync(SelfId id, CancellationToken ct = default)
    {
        var dbo = await _db.SelfIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id.Value, ct)
            .ConfigureAwait(false);
        return dbo is null ? null : Map(dbo);
    }

    public async Task<IReadOnlyList<SelfIdentity>> ListAsync(CancellationToken ct = default)
    {
        var items = await _db.SelfIdentities
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return items.Select(Map).ToList();
    }

    public async Task<SelfId> CreateAsync(SelfIdentity identity, CancellationToken ct = default)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (identity.Id.Value != 0)
        {
            throw new InvalidOperationException("CreateAsync requires identity.Id to be 0 (unsaved).");
        }

        var activeKey = identity.GetActiveKey(DateTimeOffset.UtcNow);
        var dbo = new SelfIdentityDbo
        {
            PublicIdentityId = identity.PublicIdentityId.Value,
            Name = identity.DisplayName?.Value ?? string.Empty,
            LastUsedUtc = identity.LastUsedUtc,
            ListeningPort = identity.ListeningPort.Value,
            ActiveIdentityKeySpki = activeKey?.Spki,
            ActiveIdentityKeyFingerprint = activeKey?.Fingerprint,
            RelayDeliveryRootKey = identity.RelayDeliveryRootKey?.ToArray()
        };
        _db.SelfIdentities.Add(dbo);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SelfId(dbo.Id);
    }

    public async Task SaveAsync(SelfIdentity identity, CancellationToken ct = default)
    {
        if (identity.Id.Value == 0)
        {
            // Delegate to Create for inserts
            await CreateAsync(identity, ct).ConfigureAwait(false);
        }
        else
        {
            // Update
            var dbo = await _db.SelfIdentities.FirstOrDefaultAsync(x => x.Id == identity.Id.Value, ct).ConfigureAwait(false);
            if (dbo is null)
            {
                // Upsert semantics: create if missing
                var activeKey = identity.GetActiveKey(DateTimeOffset.UtcNow);
                dbo = new SelfIdentityDbo
                {
                    Id = identity.Id.Value,
                    PublicIdentityId = identity.PublicIdentityId.Value,
                    Name = identity.DisplayName?.Value ?? string.Empty,
                    LastUsedUtc = identity.LastUsedUtc,
                    ActiveIdentityKeySpki = activeKey?.Spki,
                    ActiveIdentityKeyFingerprint = activeKey?.Fingerprint,
                    RelayDeliveryRootKey = identity.RelayDeliveryRootKey?.ToArray()
                };
                _db.SelfIdentities.Add(dbo);
            }
            else
            {
                dbo.Name = identity.DisplayName?.Value ?? dbo.Name;
                dbo.LastUsedUtc = identity.LastUsedUtc;
                dbo.ListeningPort = identity.ListeningPort.Value;
                dbo.DeviceId = identity.DeviceId.Value;
                dbo.ProfileKey = identity.CurrentProfileKey?.Span.ToArray();
                dbo.EncryptedProfileData = identity.CurrentProfileCiphertext?.Ciphertext.Span.ToArray();
                dbo.ProfileNonce = identity.CurrentProfileCiphertext?.Nonce.Span.ToArray();
                dbo.ProfileTag = identity.CurrentProfileCiphertext?.Tag.Span.ToArray();
                dbo.ProfileRevision = identity.ProfileRevision;
                dbo.RelayDeliveryRootKey = identity.RelayDeliveryRootKey?.ToArray();

                // Synchronize fingerprint whenever aggregate is saved
                var activeKey = identity.GetActiveKey(DateTimeOffset.UtcNow);
                dbo.ActiveIdentityKeySpki = activeKey?.Spki;
                dbo.ActiveIdentityKeyFingerprint = activeKey?.Fingerprint;
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static SelfIdentity Map(SelfIdentityDbo dbo)
    {
        var self = new SelfIdentity(new SelfId(dbo.Id), new PublicIdentityId(dbo.PublicIdentityId), new ListeningPort(dbo.ListeningPort));
        if (!string.IsNullOrWhiteSpace(dbo.Name)) self.SetDisplayName(dbo.Name);
        self.TouchLastUsed(dbo.LastUsedUtc);
        
        // Map profile data if present
        if (dbo.ProfileKey != null && dbo.EncryptedProfileData != null && dbo.ProfileNonce != null && dbo.ProfileTag != null)
        {
            var profileKey = ProfileKeyBytes.FromBytesOwned(dbo.ProfileKey);
            var ciphertext = EncryptedProfileDataBytes.FromBytesOwned(dbo.EncryptedProfileData);
            var nonce = ProfileNonceBytes.FromBytesOwned(dbo.ProfileNonce);
            var tag = ProfileTagBytes.FromBytesOwned(dbo.ProfileTag);
            var package = new ProfileCiphertextPackage(ciphertext, nonce, tag);
            
            // Use reflection to set private properties since there's no public setter for CurrentProfileKey/CurrentProfileCiphertext
            var profileKeyField = typeof(SelfIdentity).GetField("<CurrentProfileKey>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var profileCiphertextField = typeof(SelfIdentity).GetField("<CurrentProfileCiphertext>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var profileRevisionField = typeof(SelfIdentity).GetField("<ProfileRevision>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            profileKeyField?.SetValue(self, profileKey);
            profileCiphertextField?.SetValue(self, package);
            profileRevisionField?.SetValue(self, dbo.ProfileRevision);
        }

        // Rehydrate the aggregate's internal key list from the persisted SPKI column
        if (dbo.ActiveIdentityKeySpki != null && dbo.ActiveIdentityKeySpki.Length > 0)
        {
            self.AddKey(
                spki: dbo.ActiveIdentityKeySpki,
                notBefore: DateTimeOffset.MinValue,
                expiresAt: DateTimeOffset.MaxValue,
                now: DateTimeOffset.UtcNow);
        }

        // Rehydrate relay mode if the root key is present
        if (dbo.RelayDeliveryRootKey != null && dbo.RelayDeliveryRootKey.Length > 0)
        {
            self.EnableRelayMode(RelayRootKeyBytes.FromSpan(dbo.RelayDeliveryRootKey));
        }

        return self;
    }
}
