using System.Security.Cryptography;

namespace Percolator.Grpc.Services;

public class SelfEncryptionService
{
    //todo: append a user supplied id to the Key container name
    private const string KeyContainerName = "Percolator";
    public SelfEncryptionService(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id cannot be null or empty", nameof(id));
        var csp = new CspParameters
        {
            KeyContainerName = $"{KeyContainerName}.{id}",
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        
        //this gets or creates the key
        Identity = new RSACryptoServiceProvider(csp);
        
        Ephemeral = new RSACryptoServiceProvider(){PersistKeyInCsp = false};
    }

    public RSA Identity { get; }
    public RSA Ephemeral { get; private set; }
    public event EventHandler EphemeralChanged;
    
    //todo: periodically null this out to avoid old key use
    private RSA? _oldEphemeral = null;

    public void ChangeEphemeral()
    {
        _oldEphemeral = Ephemeral;
        Ephemeral = new RSACryptoServiceProvider(){PersistKeyInCsp = false};
        OnEphemeralChanged();
    }

    protected virtual void OnEphemeralChanged()
    {
        EphemeralChanged?.Invoke(this, EventArgs.Empty);
    }
}