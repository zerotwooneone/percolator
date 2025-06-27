using FluentAssertions;
using AutoFixture;
using Percolator.Identity;

namespace Percolator.IdentityTests
{
    [TestFixture]
    public class CredentialServiceTests
    {
        private Fixture _fixture;
        private string _testCredentialPath;

        [SetUp]
        public void SetUp()
        {
            _fixture = new Fixture();
            // Use a unique path for each test run to ensure isolation
            _testCredentialPath = Path.Combine(Path.GetTempPath(), "PercolatorTests", Path.GetRandomFileName());

            // Ensure the directory exists
            var directory = Path.GetDirectoryName(_testCredentialPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up the created file after each test
            if (File.Exists(_testCredentialPath))
            {
                File.Delete(_testCredentialPath);
            }
        }

        [Test]
        public void GetOrCreatePfxPassword_WhenNoCredentialExists_CreatesAndReturnsPassword()
        {
            // Arrange
            var sut = new CredentialService(_testCredentialPath);

            // Act
            var password = sut.GetOrCreatePfxPassword();

            // Assert
            password.Value.Should().NotBeNullOrEmpty();
            File.Exists(_testCredentialPath).Should().BeTrue();
        }

        [Test]
        public void GetOrCreatePfxPassword_WhenCredentialExists_ReturnsSamePassword()
        {
            // Arrange
            var sut = new CredentialService(_testCredentialPath);

            // Act
            var passwordFirstCall = sut.GetOrCreatePfxPassword();
            var passwordSecondCall = sut.GetOrCreatePfxPassword();

            // Assert
            passwordSecondCall.Value.Should().Be(passwordFirstCall.Value);
        }

        [Test]
        public void Protect_And_Unprotect_ShouldRoundtripSuccessfully()
        {
            // Arrange
            var sut = new CredentialService(_testCredentialPath);
            var originalData = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var protectedData = sut.Protect(originalData);
            var unprotectedData = sut.Unprotect(protectedData);

            // Assert
            unprotectedData.Should().Equal(originalData);
        }

        [Test]
        public void GetOrCreatePfxPassword_WhenCredentialFileIsCorrupt_ThrowsCryptographicException()
        {
            // Arrange
            // Create a dummy file with corrupt data
            File.WriteAllBytes(_testCredentialPath, new byte[] { 0x01, 0x02, 0x03 });
            var sut = new CredentialService(_testCredentialPath);

            // Act
            Action act = () => sut.GetOrCreatePfxPassword();

            // Assert
            act.Should().Throw<System.Security.Cryptography.CryptographicException>();
        }
    }
}
