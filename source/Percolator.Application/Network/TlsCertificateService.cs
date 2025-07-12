using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network;

public class TlsCertificateService : ITlsCertificateService
{
    private readonly IPeerRepository _peerRepository;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ICertificateFactory _certificateFactory;
    private readonly ILogger<TlsCertificateService> _logger;

    public TlsCertificateService(
        IPeerRepository peerRepository,
        IKeyManagementService keyManagementService,
        ICertificateFactory certificateFactory,
        ILogger<TlsCertificateService> logger)
    {
        _peerRepository = peerRepository;
        _keyManagementService = keyManagementService;
        _certificateFactory = certificateFactory;
        _logger = logger;
    }

    public async Task<X509Certificate2> GetOrCreateTlsCertificateAsync(string identityName, byte[] publicIdentitySigningKey)
    {
        var peer = await _peerRepository.GetByNameAsync(identityName);
        if (peer is null)
        {
            peer = new Peer(new PeerId(Guid.NewGuid()), identityName);
            await _peerRepository.AddAsync(peer);
        }

        var keys = await _keyManagementService.GetKeysAsync(identityName);
        if (keys is null)
        {
            keys = await _keyManagementService.CreateKeysAsync(identityName);
        }

        _logger.LogInformation("Requesting TLS certificate for {IdentityName}", identityName);

        return _certificateFactory.GetOrCreatePeerCertificate(
            identityName,
            keys.IdentitySigningKey,
            publicIdentitySigningKey);
    }
}
