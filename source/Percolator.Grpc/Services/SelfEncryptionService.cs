using System.Security.AccessControl;
using System.Security.Cryptography;

namespace Percolator.Grpc.Services;

public class SelfEncryptionService
{
    private const string KeyContainerName = "Percolator";
    public SelfEncryptionService()
    {
        var csp = new CspParameters
        {
            KeyContainerName = KeyContainerName,
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        
        //this gets or creates the key
        using var rsa = new RSACryptoServiceProvider(csp);
        PublicKey = rsa.ExportRSAPublicKey();
    }
    public byte[] PublicKey { get; }

    public byte[] SignHash(byte[] data)
    {
        var csp = new CspParameters
        {
            KeyContainerName = KeyContainerName,
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        
        //this gets or creates the key
        using var rsa = new RSACryptoServiceProvider(csp);
        return rsa.SignHash(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}