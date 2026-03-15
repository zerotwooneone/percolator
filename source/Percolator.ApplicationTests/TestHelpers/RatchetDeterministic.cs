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
                new RatchetIdentityKey(RemoteIdentitySpkiA),
                spkId,
                new PreKey(RemotePreKeySpkiA),
                new Signature(new byte[] { 0x09 }),
                otkId,
                null,
                null);
    }
}
