using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Protos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class ManifestTests
    {
        private ECDsa _authorKey;

        [SetUp]
        public void Setup()
        {
            _authorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }

        [TearDown]
        public void TearDown()
        {
            _authorKey?.Dispose();
        }

        private static byte[] GetCanonicalBytes(Manifest manifest)
        {
            var canonicalManifest = manifest.Clone();
            var sortedEntries = canonicalManifest.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            canonicalManifest.Entries.Clear();
            canonicalManifest.Entries.AddRange(sortedEntries);
            return canonicalManifest.ToByteArray();
        }

        [Test]
        public void ToCanonicalBytes_IsDeterministic()
        {
            var timestamp = new Timestamp { Seconds = 1704067200 }; // 2024-01-01 00:00:00 UTC
            var authorPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo());

            var manifest1 = new Manifest
            {
                AuthorIdentityPublicKey = authorPublicKey,
                TimestampUtc = timestamp,
                Entries =
                {
                    new ManifestEntry { Path = "b.txt", Type = ManifestEntry.Types.ManifestEntryType.File, Size = 100, Hash = ByteString.CopyFrom(new byte[] { 2 }) },
                    new ManifestEntry { Path = "a.txt", Type = ManifestEntry.Types.ManifestEntryType.File, Size = 200, Hash = ByteString.CopyFrom(new byte[] { 1 }) }
                }
            };

            var manifest2 = new Manifest
            {
                AuthorIdentityPublicKey = authorPublicKey,
                TimestampUtc = timestamp,
                Entries =
                {
                    new ManifestEntry { Path = "a.txt", Type = ManifestEntry.Types.ManifestEntryType.File, Size = 200, Hash = ByteString.CopyFrom(new byte[] { 1 }) },
                    new ManifestEntry { Path = "b.txt", Type = ManifestEntry.Types.ManifestEntryType.File, Size = 100, Hash = ByteString.CopyFrom(new byte[] { 2 }) }
                }
            };

            var canonicalBytes1 = GetCanonicalBytes(manifest1);
            var canonicalBytes2 = GetCanonicalBytes(manifest2);

            canonicalBytes1.Should().BeEquivalentTo(canonicalBytes2);
        }

        [Test]
        public void SignAndVerify_SuccessfulRoundtrip()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                Entries = { new ManifestEntry { Path = "file.txt", Type = ManifestEntry.Types.ManifestEntryType.File, Size = 123, Hash = ByteString.CopyFrom(SHA256.HashData(new byte[] { 1, 2, 3 })) } }
            };

            var signature = CryptoUtils.SignManifest(manifest, _authorKey);
            var signedManifest = new SignedManifest { Version = 1, Manifest = manifest, Signature = signature };

            var isValid = CryptoUtils.VerifyManifest(signedManifest);

            isValid.Should().BeTrue();
        }

        [Test]
        public void VerifyManifest_FailsWithTamperedContent()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                Entries = { new ManifestEntry { Path = "file.txt" } }
            };

            var signature = CryptoUtils.SignManifest(manifest, _authorKey);
            var signedManifest = new SignedManifest { Version = 1, Manifest = manifest, Signature = signature };

            // Tamper with the manifest after signing
            signedManifest.Manifest.Entries[0].Path = "hacked.txt";

            var isValid = CryptoUtils.VerifyManifest(signedManifest);

            isValid.Should().BeFalse();
        }

        [Test]
        public void VerifyManifest_FailsWithWrongKey()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                Entries = { new ManifestEntry { Path = "file.txt" } }
            };

            var signature = CryptoUtils.SignManifest(manifest, _authorKey);
            var signedManifest = new SignedManifest { Version = 1, Manifest = manifest, Signature = signature };

            // Replace author key with a different one
            using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            signedManifest.Manifest.AuthorIdentityPublicKey = ByteString.CopyFrom(wrongKey.ExportSubjectPublicKeyInfo());

            var isValid = CryptoUtils.VerifyManifest(signedManifest);

            isValid.Should().BeFalse();
        }

        [Test]
        public void VerifyManifest_FailsWithMissingParts()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            var signature = CryptoUtils.SignManifest(manifest, _authorKey);

            var missingManifest = new SignedManifest { Version = 1, Signature = signature };
            var missingSignature = new SignedManifest { Version = 1, Manifest = manifest };

            CryptoUtils.VerifyManifest(missingManifest).Should().BeFalse();
            CryptoUtils.VerifyManifest(missingSignature).Should().BeFalse();
        }

        [Test]
        public void VerifyManifest_FailsIfStale()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-10))
            };
            var signature = CryptoUtils.SignManifest(manifest, _authorKey);
            var signedManifest = new SignedManifest { Version = 1, Manifest = manifest, Signature = signature };

            // Verification should fail because the manifest is older than the allowed 5 minutes.
            CryptoUtils.VerifyManifest(signedManifest, TimeSpan.FromMinutes(5)).Should().BeFalse();
        }

        [Test]
        public void VerifyManifest_SucceedsIfFresh()
        {
            var manifest = new Manifest
            {
                AuthorIdentityPublicKey = ByteString.CopyFrom(_authorKey.ExportSubjectPublicKeyInfo()),
                TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            };
            var signature = CryptoUtils.SignManifest(manifest, _authorKey);
            var signedManifest = new SignedManifest { Version = 1, Manifest = manifest, Signature = signature };

            // Verification should succeed because the manifest is newer than the allowed 5 minutes.
            CryptoUtils.VerifyManifest(signedManifest, TimeSpan.FromMinutes(5)).Should().BeTrue();
        }
    }
}
