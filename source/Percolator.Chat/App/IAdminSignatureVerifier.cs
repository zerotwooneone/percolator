using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App
{
    // Verifies admin signatures over canonical payload bytes using ECDSA P-256 + SHA-256
    public interface IAdminSignatureVerifier
    {
        Task<bool> VerifyAsync(AdminPublicKey signer,
            byte[] payloadBytes,
            byte[] signature,
            CancellationToken ct);
    }
}
