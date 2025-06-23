using NUnit.Framework;
using FluentAssertions;
using Moq;
using Percolator.Identity;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

namespace Percolator.IdentityTests
{
    [TestFixture]
    public class PersistentIdentityServiceTests
    {
        private Mock<IIdentityStore> _mockIdentityStore;
        private Mock<ICredentialService> _mockCredentialService;
        private Mock<ICertificateOperations> _mockCertificateOperations;
        private Mock<IKeyManagementService> _mockKeyManagementService;
        private const string TestIdentityName = "test-identity";
        private const string TestNickname = "test-nickname";
        private const string TestPassword = "test-password";

        [SetUp]
        public void SetUp()
        {
            _mockIdentityStore = new Mock<IIdentityStore>();
            _mockCredentialService = new Mock<ICredentialService>();
            _mockCredentialService.Setup(s => s.GetOrCreatePfxPassword()).Returns(TestPassword);

            _mockCertificateOperations = new Mock<ICertificateOperations>();
            _mockCertificateOperations.Setup(co => co.CreateTlsCertificate(It.IsAny<string>()))
                .Returns((string commonName) =>
                {
                    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var request = new CertificateRequest($"cn={commonName}", ecdsa, HashAlgorithmName.SHA256);
                    var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
                    return cert;
                });

            _mockKeyManagementService = new Mock<IKeyManagementService>();
        }

        private PersistentIdentityService CreateService()
        {
            return new PersistentIdentityService(
                _mockIdentityStore.Object,
                _mockCredentialService.Object,
                _mockCertificateOperations.Object,
                _mockKeyManagementService.Object);
        }

        [Test]
        public async Task CreateIdentityAsync_WhenIdentityDoesNotExist_StoresAndReturnsIdentity()
        {
            // Arrange
            _mockIdentityStore.Setup(s => s.IdentityExistsAsync(TestIdentityName)).ReturnsAsync(false);
            var sut = CreateService();

            // Act
            var identity = await sut.CreateIdentityAsync(TestIdentityName, TestNickname);

            // Assert
            identity.Should().NotBeNull();
            identity.Name.Should().Be(TestIdentityName);
            identity.Nickname.Should().Be(TestNickname);
            identity.PfxCertificate.Value.Should().NotBeEmpty();

            _mockIdentityStore.Verify(s => s.StoreIdentityAsync(It.Is<Identity.Model.Identity>(
                i => i.Name == TestIdentityName && i.Nickname == TestNickname)), Times.Once);
            
            _mockKeyManagementService.Verify(k => k.GetOrCreateKeysAsync(TestIdentityName), Times.Once);
        }

        [Test]
        public async Task CreateIdentityAsync_WhenIdentityExists_ThrowsInvalidOperationException()
        {
            // Arrange
            _mockIdentityStore.Setup(s => s.IdentityExistsAsync(TestIdentityName)).ReturnsAsync(true);
            var sut = CreateService();

            // Act
            Func<Task> act = () => sut.CreateIdentityAsync(TestIdentityName, TestNickname);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task GetIdentityAsync_WhenIdentityExists_ReturnsIdentity()
        {
            // Arrange
            var expectedIdentity = new Identity.Model.Identity(TestIdentityName, new PfxCertificate(new byte[] { 1, 2, 3 }), "THUMBPRINT", TestNickname);
            _mockIdentityStore.Setup(s => s.GetIdentityAsync(TestIdentityName)).ReturnsAsync(expectedIdentity);
            var sut = CreateService();

            // Act
            var actualIdentity = await sut.GetIdentityAsync(TestIdentityName);

            // Assert
            actualIdentity.Should().Be(expectedIdentity);
        }

        [Test]
        public async Task GetIdentityAsync_WhenIdentityDoesNotExist_ReturnsNull()
        {
            // Arrange
            _mockIdentityStore.Setup(s => s.GetIdentityAsync("non-existent-identity")).ReturnsAsync((Identity.Model.Identity?)null);
            var sut = CreateService();

            // Act
            var result = await sut.GetIdentityAsync("non-existent-identity");

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public async Task ListIdentityNamesAsync_WhenIdentitiesExist_ReturnsNames()
        {
            // Arrange
            var expectedNames = new[] { "id1", "id2" };
            _mockIdentityStore.Setup(s => s.ListIdentityNamesAsync()).ReturnsAsync(expectedNames);
            var sut = CreateService();

            // Act
            var names = await sut.ListIdentityNamesAsync();

            // Assert
            names.Should().BeEquivalentTo(expectedNames);
        }

        [Test]
        public async Task GetIdentityKeysAsync_CallsKeyManagementService()
        {
            // Arrange
            var sut = CreateService();
            var expectedKeys = new X3dhKeys(ECDsa.Create(), ECDiffieHellman.Create(), ECDiffieHellman.Create(), ECDiffieHellman.Create());
            _mockKeyManagementService.Setup(k => k.GetOrCreateKeysAsync(TestIdentityName)).ReturnsAsync(expectedKeys);

            // Act
            var actualKeys = await sut.GetIdentityKeysAsync(TestIdentityName);

            // Assert
            actualKeys.Should().Be(expectedKeys);
            _mockKeyManagementService.Verify(k => k.GetOrCreateKeysAsync(TestIdentityName), Times.Once);
        }
    }
}
