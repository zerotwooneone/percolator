using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Percolator.Network;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Adapter that implements ITlsCertificateService by delegating to SharedCertificateManager.
    /// This allows for a smooth transition to the shared certificate model while maintaining
    /// backward compatibility with code that depends on ITlsCertificateService.
    /// </summary>
    public class SharedCertificateAdapter : ITlsCertificateService
    {
        private readonly SharedCertificateManager _certificateManager;
        private readonly ILogger<SharedCertificateAdapter> _logger;
        
        public SharedCertificateAdapter(
            SharedCertificateManager certificateManager,
            ILogger<SharedCertificateAdapter> logger)
        {
            _certificateManager = certificateManager;
            _logger = logger;
        }
        
        /// <summary>
        /// Returns the shared certificate regardless of the requested identity or public key.
        /// This adapts the per-peer certificate model to the shared certificate model.
        /// </summary>
        public Task<X509Certificate2> GetOrCreateTlsCertificateAsync(string identityName, byte[] publicIdentitySigningKey)
        {
            _logger.LogDebug("Returning shared certificate for identity {IdentityName} via adapter", identityName);
            
            // Simply return the shared certificate that we use for all communications
            var certificate = _certificateManager.GetServerCertificate();
            
            return Task.FromResult(certificate);
        }
    }
}
