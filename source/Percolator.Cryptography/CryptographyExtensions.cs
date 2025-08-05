using System.Security.Cryptography;

namespace Percolator.Cryptography;

public static class CryptographyExtensions
{
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetIdentityKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            if (publicKey.Value.Length == 64 || publicKey.Value.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (publicKey.Value.Length == 65)
                {
                    parameters.Q.X = publicKey.Value.Skip(1).Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(33).Take(32).ToArray();
                }
                else
                {
                    parameters.Q.X = publicKey.Value.Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(32).Take(32).ToArray();
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
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            if (publicKey.Value.Length == 64 || publicKey.Value.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (publicKey.Value.Length == 65)
                {
                    parameters.Q.X = publicKey.Value.Skip(1).Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(33).Take(32).ToArray();
                }
                else
                {
                    parameters.Q.X = publicKey.Value.Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(32).Take(32).ToArray();
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
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            if (publicKey.Value.Length == 64 || publicKey.Value.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (publicKey.Value.Length == 65)
                {
                    parameters.Q.X = publicKey.Value.Skip(1).Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(33).Take(32).ToArray();
                }
                else
                {
                    parameters.Q.X = publicKey.Value.Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(32).Take(32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetAgreementKey publicKey)
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            if (publicKey.Value.Length == 64 || publicKey.Value.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (publicKey.Value.Length == 65)
                {
                    parameters.Q.X = publicKey.Value.Skip(1).Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(33).Take(32).ToArray();
                }
                else
                {
                    parameters.Q.X = publicKey.Value.Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(32).Take(32).ToArray();
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
            ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
            return ecdh.PublicKey;
        }
        catch (CryptographicException)
        {
            // Try to handle raw key format
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdh.ExportParameters(false);
            if (publicKey.Value.Length == 64 || publicKey.Value.Length == 65)
            {
                // Handle potential raw EC point format (64 bytes for X,Y or 65 with format byte)
                if (publicKey.Value.Length == 65)
                {
                    parameters.Q.X = publicKey.Value.Skip(1).Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(33).Take(32).ToArray();
                }
                else
                {
                    parameters.Q.X = publicKey.Value.Take(32).ToArray();
                    parameters.Q.Y = publicKey.Value.Skip(32).Take(32).ToArray();
                }
                ecdh.ImportParameters(parameters);
                return ecdh.PublicKey;
            }
            throw;
        }
    }
}
