namespace Percolator.Cryptography;

public static class Oids
{
    /// <summary>
    /// OID for the X.509 extension field containing the peer's public identity signing key.
    /// The value is in a private enterprise number space for documentation/example purposes.
    /// 1.3.6.1.4.1 (iso.org.dod.internet.private.enterprise)
    /// .58824 (a fictional PEN for this project)
    /// .1.1 (a project-specific identifier for the identity key)
    /// </summary>
    public const string PeerIdentityKey = "1.3.6.1.4.1.58824.1.1";
}
