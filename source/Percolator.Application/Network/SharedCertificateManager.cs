using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Manages the shared certificate for gRPC communication.
    /// This is a temporary solution until the Trust-On-First-Use (TOFU) implementation is complete.
    /// </summary>
    public class SharedCertificateManager : IDisposable
    {
        private readonly ILogger<SharedCertificateManager> _logger;
        private X509Certificate2? _certificate;
        private bool _disposed;

        // Certificate file names
        public const string ServerCertificateFileName = "percolator-shared-cert.pfx";
        public const string ClientCertificateFileName = "percolator-shared-cert.cer";
        public const string CertificatePassword = "percolator-dev-cert";

        public SharedCertificateManager(ILogger<SharedCertificateManager> logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Locates the certificate file in standard locations
        /// </summary>
        /// <param name="fileName">The name of the certificate file</param>
        /// <returns>Path to the certificate file</returns>
        public string FindCertificateFile(string fileName)
        {
            // Check several standard locations for the certificate file
            
            // 1. Check directly in certificates directory under application base directory
            var certificatesPath = Path.Combine(AppContext.BaseDirectory, "certificates", fileName);
            if (File.Exists(certificatesPath))
            {
                _logger.LogDebug("Found certificate at: {CertificatePath}", certificatesPath);
                return certificatesPath;
            }
            
            // 2. Check if we're in a dev environment (running from source)
            var devPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "certificates", fileName);
            var normalizedDevPath = Path.GetFullPath(devPath);
            if (File.Exists(normalizedDevPath))
            {
                _logger.LogDebug("Found certificate at dev location: {CertificatePath}", normalizedDevPath);
                return normalizedDevPath;
            }
            
            // 3. Check in the Node project certificates directory
            var nodeCertPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Percolator.Node", "certificates", fileName);
            var normalizedNodePath = Path.GetFullPath(nodeCertPath);
            if (File.Exists(normalizedNodePath))
            {
                _logger.LogDebug("Found certificate in Node project: {CertificatePath}", normalizedNodePath);
                return normalizedNodePath;
            }
            
            // 4. Check project root as fallback (old location)
            var rootPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName);
            var normalizedRootPath = Path.GetFullPath(rootPath);
            if (File.Exists(normalizedRootPath))
            {
                _logger.LogDebug("Found certificate at root location: {CertificatePath}", normalizedRootPath);
                return normalizedRootPath;
            }
            
            // Log paths we checked
            _logger.LogWarning("Certificate {FileName} not found in any standard locations. Checked: " +
                               "{Path1}, {Path2}, {Path3}, {Path4}", 
                fileName, certificatesPath, normalizedDevPath, normalizedNodePath, normalizedRootPath);
                
            // Return the default path (which likely won't exist)
            return certificatesPath;
        }

        /// <summary>
        /// Gets the server certificate with private key for hosting gRPC services
        /// </summary>
        /// <param name="certificatePath">Optional path to the certificate file (.pfx)</param>
        /// <param name="password">Password for the certificate</param>
        /// <returns>The loaded certificate</returns>
        public X509Certificate2 GetServerCertificate(string? certificatePath = null, string? password = null)
        {
            try
            {
                if (_certificate == null)
                {
                    string finalPath = certificatePath ?? FindCertificateFile(ServerCertificateFileName);
                    string finalPassword = password ?? CertificatePassword;
                    
                    _logger.LogInformation("Loading server certificate from {CertificatePath}", finalPath);
                    
                    // Ensure the certificate exists
                    if (!File.Exists(finalPath))
                    {
                        throw new FileNotFoundException($"Certificate file not found: {finalPath}");
                    }

                    try
                    {
                        // Use ephemeral key set to avoid storing the private key on disk
                        // and use UserKeySet rather than MachineKeySet to avoid permission issues
                        const X509KeyStorageFlags keyFlags = 
                            X509KeyStorageFlags.UserKeySet | 
                            X509KeyStorageFlags.Exportable | 
                            X509KeyStorageFlags.EphemeralKeySet;
                        
                        // Load with flags appropriate for server use where we need the private key
                        _certificate = new X509Certificate2(
                            finalPath, 
                            finalPassword,
                            keyFlags
                        );
                        
                        if (!_certificate.HasPrivateKey)
                        {
                            _logger.LogError("Loaded certificate does not have a private key");
                            throw new InvalidOperationException("Certificate loaded without private key, cannot be used for TLS server");
                        }
                        
                        _logger.LogInformation("Server certificate loaded successfully with private key. Thumbprint: {Thumbprint}, Subject: {Subject}", 
                            _certificate.Thumbprint, _certificate.Subject);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load certificate with private key: {Message}", ex.Message);
                        throw;
                    }
                }
                
                return _certificate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load server certificate: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Gets the client certificate for validating server identity
        /// </summary>
        /// <param name="certificatePath">Optional path to the public certificate file (.cer)</param>
        /// <returns>The loaded certificate</returns>
        public X509Certificate2 GetClientCertificate(string? certificatePath = null)
        {
            try
            {
                // Always use the server certificate that has the private key
                // for mutual TLS client authentication
                return GetServerCertificate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load client certificate: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Disposes the managed certificate resources
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _certificate?.Dispose();
                    _certificate = null;
                }
                
                _disposed = true;
            }
        }
    }
}
