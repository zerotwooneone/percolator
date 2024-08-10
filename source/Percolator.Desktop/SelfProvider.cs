using System.Security.Cryptography;

namespace Percolator.Desktop;

public class SelfProvider : ISelfProvider, ISelfInitializer
{
    private SelfModel? _self;
    
    public SelfModel GetSelf()
    {
        if (_self is null)
        {
            throw new InvalidOperationException("self has not been initialized");
        }
        return _self;
    }
    private const string KeyContainerName = "Percolator";
    public void InitSelf(Guid identitySuffix)
    {
        if (identitySuffix == Guid.Empty)
            throw new ArgumentException("identitySuffix cannot be null or empty", nameof(identitySuffix));
        var csp = new CspParameters
        {
            KeyContainerName = $"{KeyContainerName}.{identitySuffix}",
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        var identity = new RSACryptoServiceProvider(csp);
        _self = new SelfModel(identitySuffix, identity);
    }
}