using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class ManifestTests
    {
        private ECDsa _privateKey;
        private ECDsa _publicKey;
        private byte[] _publicKeyBytes;

        [SetUp]
        public void Setup()
        {
            (_privateKey, _publicKey) = CryptoUtils.GenerateNewKeys();
            _publicKeyBytes = _publicKey.ExportSubjectPublicKeyInfo();
        }

        [TearDown]
        public void Teardown()
        {
            _privateKey?.Dispose();
            _publicKey?.Dispose();
        }

        private byte[] GetCanonicalBytes(Manifest manifest)
        {
            var canonicalManifest = manifest.Clone();
            var sortedEntries = canonicalManifest.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            canonicalManifest.Entries.Clear();
            canonicalManifest.Entries.AddRange(sortedEntries);
            return canonicalManifest.ToByteArray();
        }

        private Manifest CreateTestManifest()
        {
            return new Manifest
            {
                SignerCertificateDer = ByteString.CopyFrom(_publicKeyBytes),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                Entries =
                {
                    new ManifestEntry { Path = "b.txt", Hash = ByteString.CopyFrom(new byte[] { 2 }) },
                    new ManifestEntry { Path = "a.txt", Hash = ByteString.CopyFrom(new byte[] { 1 }) }
                }
            };
        }

        [Test]
        public void Signature_Verification_Should_Succeed_For_Valid_Signature()
        {
            // Arrange
            var manifest = CreateTestManifest();
            var canonicalBytes = GetCanonicalBytes(manifest);

            // Act
            var signature = CryptoUtils.Sign(canonicalBytes, _privateKey);
            var isValid = CryptoUtils.Verify(canonicalBytes, signature, _publicKey);

            // Assert
            isValid.Should().BeTrue();
        }

        [Test]
        public void Signature_Verification_Should_Fail_For_Tampered_Data()
        {
            // Arrange
            var manifest = CreateTestManifest();
            var canonicalBytes = GetCanonicalBytes(manifest);
            var signature = CryptoUtils.Sign(canonicalBytes, _privateKey);

            // Act
            var tamperedManifest = manifest.Clone();
            tamperedManifest.Entries[0].Path = "c.txt";
            var tamperedBytes = GetCanonicalBytes(tamperedManifest);
            var isValid = CryptoUtils.Verify(tamperedBytes, signature, _publicKey);

            // Assert
            isValid.Should().BeFalse();
        }

        [Test]
        public void Canonical_Serialization_Should_Be_Deterministic()
        {
            // Arrange
            var manifest1 = new Manifest
            {
                Entries =
                {
                    new ManifestEntry { Path = "b.txt" },
                    new ManifestEntry { Path = "a.txt" }
                }
            };

            var manifest2 = new Manifest
            {
                Entries =
                {
                    new ManifestEntry { Path = "a.txt" },
                    new ManifestEntry { Path = "b.txt" }
                }
            };

            // Act
            var bytes1 = GetCanonicalBytes(manifest1);
            var bytes2 = GetCanonicalBytes(manifest2);

            // Assert
            bytes1.Should().Equal(bytes2);
        }
    }
}
