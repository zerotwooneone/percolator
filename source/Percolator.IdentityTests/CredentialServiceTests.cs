using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;

namespace Percolator.IdentityTests
{
    [TestFixture]
    public class CredentialServiceTests
    {
        private Fixture _fixture;
        private Mock<ILogger<CredentialService>> _loggerMock;
        private CredentialService _sut;

        [SetUp]
        public void SetUp()
        {
            _fixture = new Fixture();
            _loggerMock = new Mock<ILogger<CredentialService>>();
            _sut = new CredentialService(_loggerMock.Object);

            // Clean up from previous runs
            var identityName = _fixture.Create<string>();
            var basePath = IdentityPathHelper.GetBasePath(identityName);
            var credsPath = Path.Combine(basePath, "creds");
            if (Directory.Exists(credsPath))
            {
                Directory.Delete(credsPath, true);
            }
        }

        [Test]
        public async Task GetOrCreateCredentialAsync_WhenCredentialDoesNotExist_CreatesAndReturnsNewCredential()
        {
            // Arrange
            var identityName = _fixture.Create<string>();
            var purpose = _fixture.Create<string>();

            // Act
            var credential1 = await _sut.GetOrCreateCredentialAsync(identityName, purpose);
            var credential2 = await _sut.GetOrCreateCredentialAsync(identityName, purpose);

            // Assert
            credential1.Should().NotBeNullOrEmpty();
            credential1.Should().Be(credential2);
            var basePath = IdentityPathHelper.GetBasePath(identityName);
            var credPath = Path.Combine(basePath, "creds", $"{purpose}.cred");
            File.Exists(credPath).Should().BeTrue();
        }

        [Test]
        public async Task GetCredentialAsync_WhenCredentialExists_ReturnsExistingCredential()
        {
            // Arrange
            var identityName = _fixture.Create<string>();
            var purpose = _fixture.Create<string>();
            var expectedCredential = await _sut.GetOrCreateCredentialAsync(identityName, purpose);

            // Act
            var actualCredential = await _sut.GetCredentialAsync(identityName, purpose);

            // Assert
            actualCredential.Should().Be(expectedCredential);
        }

        [Test]
        public async Task GetCredentialAsync_WhenCredentialDoesNotExist_ReturnsNull()
        {
            // Arrange
            var identityName = _fixture.Create<string>();
            var purpose = _fixture.Create<string>();

            // Act
            var credential = await _sut.GetCredentialAsync(identityName, purpose);

            // Assert
            credential.Should().BeNull();
        }

        [Test]
        public async Task Protect_And_Unprotect_ShouldRoundtripSuccessfully()
        {
            // Arrange
            var originalData = System.Text.Encoding.UTF8.GetBytes("super secret data");

            // Act
            var protectedData = _sut.Protect(originalData);
            var unprotectedData = _sut.Unprotect(protectedData);

            // Assert
            unprotectedData.Should().Equal(originalData);
        }

        [Test]
        public async Task GetOrCreateCredentialAsync_WhenCredentialFileIsCorrupt_ThrowsCryptographicException()
        {
            // Arrange
            // Create a dummy file with corrupt data
            var identityName = _fixture.Create<string>();
            var purpose = _fixture.Create<string>();
            var basePath = IdentityPathHelper.GetBasePath(identityName);
            var credPath = Path.Combine(basePath, "creds", $"{purpose}.cred");
            File.WriteAllBytes(credPath, new byte[] { 0x01, 0x02, 0x03 });

            // Act
            Func<Task> act = async () => await _sut.GetOrCreateCredentialAsync(identityName, purpose);

            // Assert
            await act.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
        }
    }
}
