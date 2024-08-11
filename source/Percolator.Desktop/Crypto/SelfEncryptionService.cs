using System.Security.Cryptography;

namespace Percolator.Desktop.Crypto;

public class SelfEncryptionService
{
    
    public SelfEncryptionService(string id)
    {
        Ephemeral = new RSACryptoServiceProvider(){PersistKeyInCsp = false};
    }
    public RSA Ephemeral { get; private set; }
    public event EventHandler? EphemeralChanged;
    
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