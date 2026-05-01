using Percolator.Cryptography;

namespace Percolator.ApplicationTests.TestHelpers
{
    public static class RatchetDeterministic
    {
        public static byte[] Bytes(string hex) => Convert.FromHexString(hex);

        // Fixed, deterministic SPKI-like byte payloads for tests
        public static readonly byte[] RemoteIdentitySpkiA = Bytes("A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1");
        public static readonly byte[] RemotePreKeySpkiA   = Bytes("B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2");

        public static PreKeyBundle MakeBundleA(Guid spkId, Guid? otkId = null)
            => new PreKeyBundle(
                RatchetIdentityKey.FromBytes(RemoteIdentitySpkiA),
                spkId,
                PreKey.FromBytes(RemotePreKeySpkiA),
                Percolator.Cryptography.Signature.FromBytes(new byte[64]),
                otkId,
                null,
                null);
    }
}
