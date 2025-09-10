using System.Security.Cryptography;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;

namespace Percolator.Infrastructure.Cryptography
{
    // ECDSA P-256 + SHA-256 over canonical payload bytes; public key provided as SPKI bytes
    public sealed class AdminSignatureVerifier : IAdminSignatureVerifier
    {
        public Task<bool> VerifyAsync(AdminPublicKey signer, byte[] payloadBytes, byte[] signature, CancellationToken ct)
        {
            // Validate inputs
            if (payloadBytes is null || payloadBytes.Length == 0) return Task.FromResult(false);
            if (signature is null || signature.Length == 0) return Task.FromResult(false);

            try
            {
                using var ecdsa = ECDsa.Create();
                // Import SPKI public key
                ecdsa.ImportSubjectPublicKeyInfo(signer.Bytes, out _);
                var ok = ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256);
                return Task.FromResult(ok);
            }
            catch (CryptographicException)
            {
                return Task.FromResult(false);
            }
        }
    }
}
