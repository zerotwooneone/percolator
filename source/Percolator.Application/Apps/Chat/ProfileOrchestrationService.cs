using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Apps.Chat;

public sealed class ProfileOrchestrationService : IProfileOrchestrationService
{
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly IProfileCryptographyService _profileCryptographyService;
    private readonly ISecureRandom _secureRandom;
    private readonly ILogger<ProfileOrchestrationService> _logger;

    public ProfileOrchestrationService(
        ISelfIdentityRepository selfIdentityRepository,
        IPeerIdentityRepository peerIdentityRepository,
        IProfileCryptographyService profileCryptographyService,
        ISecureRandom secureRandom,
        ILogger<ProfileOrchestrationService> logger)
    {
        _selfIdentityRepository = selfIdentityRepository;
        _peerIdentityRepository = peerIdentityRepository;
        _profileCryptographyService = profileCryptographyService;
        _secureRandom = secureRandom;
        _logger = logger;
    }

    public async Task UpdateLocalProfileAsync(string newDisplayName, CancellationToken ct)
    {
        var selfIdentity = await _selfIdentityRepository.GetMostRecentAsync(ct);
        if (selfIdentity is null)
        {
            throw new InvalidOperationException("No active self identity found");
        }

        // Generate secure random profile key
        var randomBytes = _secureRandom.GetBytes(32);
        var identityProfileKey = Percolator.Identity.ProfileKeyBytes.FromBytesOwned(randomBytes);
        var cryptoProfileKey = Percolator.Cryptography.ProfileKeyBytes.FromBytesOwned(randomBytes);

        // Serialize profile data to Protobuf
        var profileData = new ProfileData { DisplayName = newDisplayName };
        var profileDataBytes = profileData.ToByteArray();
        var cryptoProfilePlaintext = Percolator.Cryptography.ProfilePlaintextBytes.FromBytesOwned(profileDataBytes);

        // Encrypt using Cryptography service
        var encryptionResult = _profileCryptographyService.EncryptData(cryptoProfilePlaintext, cryptoProfileKey);

        // Map back to Identity domain
        var identityCiphertext = Percolator.Identity.EncryptedProfileDataBytes.FromBytesOwned(encryptionResult.Ciphertext.Span.ToArray());
        var identityNonce = Percolator.Identity.ProfileNonceBytes.FromBytesOwned(encryptionResult.Nonce.Span.ToArray());
        var identityTag = Percolator.Identity.ProfileTagBytes.FromBytesOwned(encryptionResult.Tag.Span.ToArray());

        var package = new Percolator.Identity.ProfileCiphertextPackage(identityCiphertext, identityNonce, identityTag);

        // Update and save
        selfIdentity.SetDisplayName(newDisplayName);
        selfIdentity.CommitProfileUpdate(identityProfileKey, package);
        await _selfIdentityRepository.SaveAsync(selfIdentity, ct);

        _logger.LogInformation("Profile updated to revision {Revision}", selfIdentity.ProfileRevision);
    }

    public async Task AttachProfileDataIfRequiredAsync(ChatEnvelope envelope, PeerId recipientPeerId, CancellationToken ct)
    {
        var selfIdentity = await _selfIdentityRepository.GetMostRecentAsync(ct);
        if (selfIdentity is null || selfIdentity.CurrentProfileCiphertext is null)
        {
            return; // No profile data to attach
        }

        var peerIdentity = await _peerIdentityRepository.GetByIdAsync(recipientPeerId, ct);
        if (peerIdentity is null)
        {
            return; // Unknown peer
        }

        // Check if recipient needs profile update
        if (selfIdentity.ProfileRevision <= peerIdentity.LastKnownProfileRevision)
        {
            return; // Recipient already has latest profile
        }

        // Attach profile data to envelope
        envelope.ProfileKey = Google.Protobuf.ByteString.CopyFrom(selfIdentity.CurrentProfileKey.Span.ToArray());
        envelope.EncryptedProfileData = Google.Protobuf.ByteString.CopyFrom(selfIdentity.CurrentProfileCiphertext.Ciphertext.Span.ToArray());
        envelope.ProfileNonce = Google.Protobuf.ByteString.CopyFrom(selfIdentity.CurrentProfileCiphertext.Nonce.Span.ToArray());
        envelope.ProfileTag = Google.Protobuf.ByteString.CopyFrom(selfIdentity.CurrentProfileCiphertext.Tag.Span.ToArray());
        envelope.ProfileRevision = selfIdentity.ProfileRevision;

        _logger.LogDebug("Attached profile data (revision {Revision}) for peer {PeerId}", selfIdentity.ProfileRevision, recipientPeerId);
    }

    public async Task ProcessInboundProfileDataAsync(ChatEnvelope envelope, PeerId senderPeerId, CancellationToken ct)
    {
        if (!envelope.HasProfileKey || !envelope.HasEncryptedProfileData || 
            !envelope.HasProfileNonce || !envelope.HasProfileTag || 
            !envelope.HasProfileRevision)
        {
            return; // No profile data in envelope
        }

        var peerIdentity = await _peerIdentityRepository.GetByIdAsync(senderPeerId, ct);
        if (peerIdentity is null)
        {
            _logger.LogWarning("Received profile data from unknown peer {PeerId}", senderPeerId);
            return;
        }

        // Check if we need to update
        if (envelope.ProfileRevision <= peerIdentity.LastKnownProfileRevision)
        {
            return; // Already have this revision or newer
        }

        // Extract bytes
        var profileKeyBytes = envelope.ProfileKey.ToByteArray();
        var ciphertextBytes = envelope.EncryptedProfileData.ToByteArray();
        var nonceBytes = envelope.ProfileNonce.ToByteArray();
        var tagBytes = envelope.ProfileTag.ToByteArray();

        // Translate to Cryptography domain
        var cryptoProfileKey = Percolator.Cryptography.ProfileKeyBytes.FromBytesOwned(profileKeyBytes);
        var cryptoCiphertext = Percolator.Cryptography.EncryptedProfileDataBytes.FromBytesOwned(ciphertextBytes);
        var cryptoNonce = Percolator.Cryptography.ProfileNonceBytes.FromBytesOwned(nonceBytes);
        var cryptoTag = Percolator.Cryptography.ProfileTagBytes.FromBytesOwned(tagBytes);

        // Decrypt
        var plaintext = _profileCryptographyService.DecryptData(cryptoCiphertext, cryptoNonce, cryptoTag, cryptoProfileKey);

        // Deserialize Protobuf
        var profileData = ProfileData.Parser.ParseFrom(plaintext.Span.ToArray());

        // Update peer identity
        peerIdentity.SetDisplayName(profileData.DisplayName);
        peerIdentity.UpdateLastKnownProfileRevision(envelope.ProfileRevision);
        await _peerIdentityRepository.SaveAsync(peerIdentity, ct);

        _logger.LogInformation("Updated profile for peer {PeerId} to revision {Revision}", senderPeerId, envelope.ProfileRevision);
    }
}
