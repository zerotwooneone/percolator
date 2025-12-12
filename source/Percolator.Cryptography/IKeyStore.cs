namespace Percolator.Cryptography;

// Port for key management used by domain aggregates.
public interface IKeyStore
{
    PrivatePreKey GetIdentityPrivateKey();
    PrivatePreKey GetSignedPreKeyPrivate(string signedPreKeyId);
    PrivatePreKey? TryGetOneTimePreKeyPrivate(string oneTimePreKeyId);
}
