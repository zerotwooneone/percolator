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
        private string _testIdentitiesPath;
        private const string TestIdentityName = "test-identity";
        private const string TestPassword = "test-password";

        [SetUp]
        public void SetUp()
        {
            _mockCredentialService = new Mock<ICredentialService>();
            _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(TestPassword);

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
            return new PersistentIdentityService(_mockCredentialService.Object, _testIdentitiesPath);
        }

        [Test]
        public void GetOrCreateIdentity_WhenNoCertificateExists_CreatesAndReturnsCertificate()
        {
            // Arrange
            var sut = CreateService();

            // Act
            var certificate = sut.CreateIdentity(TestIdentityName);

            // Assert
            certificate.Should().NotBeNull();
            certificate.SubjectName.Name.Should().Contain($"CN={TestIdentityName}");
            File.Exists(Path.Combine(_testIdentitiesPath, $"{TestIdentityName}.pfx")).Should().BeTrue();
        }

        [Test]
        public void GetOrCreateIdentity_WhenCertificateExists_LoadsAndReturnsCertificate()
        {
            // Arrange
            var sut = CreateService();
            var firstCert = sut.CreateIdentity(TestIdentityName);

            // Act
            var secondCert = sut.CreateIdentity(TestIdentityName);

            // Assert
            secondCert.Thumbprint.Should().Be(firstCert.Thumbprint);
        }

        [Test]
        public void GetOrCreateIdentity_WhenCertificateIsCorrupt_ThrowsCryptographicException()
        {
            // Arrange
            var sut = CreateService();
            var certPath = Path.Combine(_testIdentitiesPath, $"{TestIdentityName}.pfx");
            File.WriteAllBytes(certPath, new byte[] { 1, 2, 3 }); // Corrupt file

            // Act
            Action act = () => sut.CreateIdentity(TestIdentityName);

            // Assert
            act.Should().Throw<CryptographicException>();
        }
    }
}
