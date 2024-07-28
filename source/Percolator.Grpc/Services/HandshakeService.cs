using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Percolator.Grpc.Services;

public class HandshakeService
{
    private readonly ILogger<HandshakeService> _logger;

    public HandshakeService(ILogger<HandshakeService> logger)
    {
        _logger = logger;
    }

    public bool TryCreateHandshake(
        RSA key, 
        [NotNullWhen(true)]out Handshake handshake)
    {
        using (Aes aes = Aes.Create())
        {
            var keyFormatter = new RSAOAEPKeyExchangeFormatter(key);
            var encryptedSessionKey = keyFormatter.CreateKeyExchange(aes.Key, typeof(Aes));
            handshake = new Handshake(aes.IV, encryptedSessionKey);
            return true;
        }
    }
}