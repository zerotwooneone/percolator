using Microsoft.Extensions.Logging;
using Percolator.Identity.Model;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Identity;

public class PersistentIdentityService : IIdentityService
{
    private readonly IIdentityStore _identityStore;
    private readonly ICredentialService _credentialService;
    private readonly ICertificateOperations _certificateOperations;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ILogger<PersistentIdentityService> _logger;

    public PersistentIdentityService(
        IIdentityStore identityStore,
        ICredentialService credentialService,
        ICertificateOperations certificateOperations,
        IKeyManagementService keyManagementService,
        ILogger<PersistentIdentityService> logger)
    {
        _identityStore = identityStore;
        _credentialService = credentialService;
        _certificateOperations = certificateOperations;
        _keyManagementService = keyManagementService;
        _logger = logger;
    }

    public async Task<IdentityRecord> CreateIdentityAsync(string name, string? nickname, CancellationToken cancellationToken = default)
    {
        if (await _identityStore.IdentityExistsAsync(name, cancellationToken))
        {
            throw new System.InvalidOperationException($"An identity with the name '{name}' already exists.");
        }

        var pfxPassword = _credentialService.GetOrCreatePfxPassword();

        var newCert = _certificateOperations.CreateTlsCertificate(name);
        var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword.Value);
        var pfxCertificate = new PfxCertificate(pfxBytes);

        await _keyManagementService.GetOrCreateKeysAsync(name);

        var identity = new IdentityRecord(name, pfxCertificate, newCert.Thumbprint, nickname);
        await _identityStore.StoreIdentityAsync(identity, cancellationToken);
        _logger.LogInformation("Created identity {IdentityName} with thumbprint {Thumbprint}", name, newCert.Thumbprint);

        return identity;
    }

    public async Task<IdentityRecord?> GetIdentityRecordAsync(string name, CancellationToken cancellationToken = default)
    {
        return await _identityStore.GetIdentityAsync(name, cancellationToken);
    }

    public async Task<Certificate> LoadIdentityAsync(string name, CancellationToken cancellationToken = default)
    {
        var identity = await _identityStore.GetIdentityAsync(name, cancellationToken);
        if (identity is null)
        {
            throw new KeyNotFoundException($"Identity '{name}' not found.");
        }

        var pfxPassword = _credentialService.GetOrCreatePfxPassword();
        var certificate = X509CertificateLoader.LoadPkcs12(identity.PfxCertificate.Value, pfxPassword.Value, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        
        return new Certificate(certificate);
    }

    public async Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _identityStore.ListIdentityNamesAsync(cancellationToken);
    }
}
