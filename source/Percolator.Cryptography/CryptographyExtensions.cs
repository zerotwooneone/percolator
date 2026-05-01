using System.Security.Cryptography;

namespace Percolator.Cryptography;

public static class CryptographyExtensions
{
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetIdentityKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Span, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            var pk = publicKey.ToArray();
            if (pk.Length == 64 || pk.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (pk.Length == 65)
                {
                    parameters.Q.X = pk.AsSpan(1, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(33, 32).ToArray();
                }
                else
                {
                    parameters.Q.X = pk.AsSpan(0, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(32, 32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetEphemeralKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Span, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            var pk = publicKey.ToArray();
            if (pk.Length == 64 || pk.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (pk.Length == 65)
                {
                    parameters.Q.X = pk.AsSpan(1, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(33, 32).ToArray();
                }
                else
                {
                    parameters.Q.X = pk.AsSpan(0, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(32, 32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this PreKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Span, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            var pk = publicKey.ToArray();
            if (pk.Length == 64 || pk.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (pk.Length == 65)
                {
                    parameters.Q.X = pk.AsSpan(1, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(33, 32).ToArray();
                }
                else
                {
                    parameters.Q.X = pk.AsSpan(0, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(32, 32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this OneTimeKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Span, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            var pk = publicKey.ToArray();
            if (pk.Length == 64 || pk.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (pk.Length == 65)
                {
                    parameters.Q.X = pk.AsSpan(1, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(33, 32).ToArray();
                }
                else
                {
                    parameters.Q.X = pk.AsSpan(0, 32).ToArray();
                    parameters.Q.Y = pk.AsSpan(32, 32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
}
