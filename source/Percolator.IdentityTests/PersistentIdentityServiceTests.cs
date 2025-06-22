using NUnit.Framework;
using FluentAssertions;
using Moq;
using Percolator.Identity;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.IdentityTests
{
    [TestFixture]
    public class PersistentIdentityServiceTests
    {
        private Mock<ICredentialService> _mockCredentialService;
        private Mock<ICertificateOperations> _mockCertificateOperations;
        private Mock<IKeyManagementService> _mockKeyManagementService;
        private string _testIdentitiesPath;
        private const string TestIdentityName = "test-identity";
        private const string TestPassword = "test-password";

        [SetUp]
        public void SetUp()
        {
            _mockCredentialService = new Mock<ICredentialService>();
            _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(TestPassword);

            _mockCertificateOperations = new Mock<ICertificateOperations>();
            _mockCertificateOperations.Setup(co => co.CreateTlsCertificate(It.IsAny<string>()))
                .Returns((string commonName) =>
                {
                    // For the test, we can just create a simple self-signed cert.
                    // The actual implementation is tested in the Cryptography library.
                    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var request = new CertificateRequest($"cn={commonName}", ecdsa, HashAlgorithmName.SHA256);
                    var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
                    return cert;
                });

            _mockKeyManagementService = new Mock<IKeyManagementService>();

            _testIdentitiesPath = Path.Combine(Path.GetTempPath(), "PercolatorTests", Path.GetRandomFileName());
            Directory.CreateDirectory(_testIdentitiesPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testIdentitiesPath))
            {
                Directory.Delete(_testIdentitiesPath, true);
            }
        }

        private PersistentIdentityService CreateService()
        {
            return new PersistentIdentityService(_mockCredentialService.Object, _mockCertificateOperations.Object, _mockKeyManagementService.Object, _testIdentitiesPath);
        }

        [Test]
        public void CreateIdentity_WhenIdentityDoesNotExist_CreatesAndReturnsCertificate()
        {
            // Arrange
            var sut = CreateService();

            // Act
            var certificate = sut.CreateIdentity(TestIdentityName);

            // Assert
            certificate.Should().NotBeNull();
            certificate.SubjectName.Name.Should().Contain($"CN={TestIdentityName}");
            File.Exists(Path.Combine(_testIdentitiesPath, $"{TestIdentityName}.pfx")).Should().BeTrue();
            _mockKeyManagementService.Verify(k => k.GetOrCreateKeys(TestIdentityName), Times.Once);
        }

        [Test]
        public void CreateIdentity_WhenIdentityExists_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = CreateService();
            sut.CreateIdentity(TestIdentityName); // Create it once

            // Act
            Action act = () => sut.CreateIdentity(TestIdentityName); // Try to create it again

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void GetIdentityCertificate_WhenCertificateExists_LoadsAndReturnsCertificate()
        {
            // Arrange
            var sut = CreateService();
            var firstCert = sut.CreateIdentity(TestIdentityName);

            // Act
            var secondCert = sut.GetIdentityCertificate(TestIdentityName);

            // Assert
            secondCert.Thumbprint.Should().Be(firstCert.Thumbprint);
        }

        [Test]
        public void GetIdentityCertificate_WhenCertificateIsCorrupt_ThrowsCryptographicException()
        {
            // Arrange
            var sut = CreateService();
            var certPath = Path.Combine(_testIdentitiesPath, $"{TestIdentityName}.pfx");
            File.WriteAllBytes(certPath, new byte[] { 1, 2, 3 }); // Corrupt file

            // Act
            Action act = () => sut.GetIdentityCertificate(TestIdentityName);

            // Assert
            act.Should().Throw<CryptographicException>();
        }

        [Test]
        public void GetIdentityCertificate_WhenIdentityDoesNotExist_ThrowsFileNotFoundException()
        {
            // Arrange
            var sut = CreateService();

            // Act
            Action act = () => sut.GetIdentityCertificate("non-existent-identity");

            // Assert
            act.Should().Throw<FileNotFoundException>();
        }

        [Test]
        public void ListIdentityNames_WhenIdentitiesExist_ReturnsNames()
        {
            // Arrange
            var sut = CreateService();
            sut.CreateIdentity("id1");
            sut.CreateIdentity("id2");

            // Act
            var names = sut.ListIdentityNames();

            // Assert
            names.Should().BeEquivalentTo("id1", "id2");
        }

        [Test]
        public void GetIdentityKeys_CallsKeyManagementService()
        {
            // Arrange
            var sut = CreateService();
            var expectedKeys = new X3dhKeys(ECDsa.Create(), ECDiffieHellman.Create(), ECDiffieHellman.Create(), ECDiffieHellman.Create());
            _mockKeyManagementService.Setup(k => k.GetOrCreateKeys(TestIdentityName)).Returns(expectedKeys);

            // Act
            var actualKeys = sut.GetIdentityKeys(TestIdentityName);

            // Assert
            actualKeys.Should().Be(expectedKeys);
            _mockKeyManagementService.Verify(k => k.GetOrCreateKeys(TestIdentityName), Times.Once);
        }
    }
}
