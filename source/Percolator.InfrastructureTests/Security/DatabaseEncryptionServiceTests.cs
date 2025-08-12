using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Security;
using System.IO;

namespace Percolator.InfrastructureTests.Security
{
    public class DatabaseEncryptionServiceTests
    {
        private string _testDirectory;
        private Mock<IOptions<StorageOptions>> _mockStorageOptions;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "percolator_tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);

            var storageOptions = new StorageOptions { Path = _testDirectory };
            _mockStorageOptions = new Mock<IOptions<StorageOptions>>();
            _mockStorageOptions.Setup(o => o.Value).Returns(storageOptions);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        [Test]
        public void GetDatabasePassword_ShouldCreateAndSaveNewPassword_WhenKeyFileDoesNotExist()
        {
            // Arrange
            var service = new DatabaseEncryptionService(_mockStorageOptions.Object);
            var keyFilePath = Path.Combine(_testDirectory, "db.key");

            // Act
            var password = service.GetDatabasePassword();

            // Assert
            password.Should().NotBeNullOrEmpty();
            File.Exists(keyFilePath).Should().BeTrue();
        }

        [Test]
        public void GetDatabasePassword_ShouldLoadPasswordFromExistingFile()
        {
            // Arrange
            var service1 = new DatabaseEncryptionService(_mockStorageOptions.Object);
            var password_first_run = service1.GetDatabasePassword();

            var service2 = new DatabaseEncryptionService(_mockStorageOptions.Object);

            // Act
            var password_second_run = service2.GetDatabasePassword();

            // Assert
            password_second_run.Should().Be(password_first_run);
        }

        [Test]
        public void GetDatabasePassword_ShouldReturnSamePassword_OnSubsequentCalls()
        {
            // Arrange
            var service = new DatabaseEncryptionService(_mockStorageOptions.Object);
            var password_first_call = service.GetDatabasePassword();

            // Act
            var password_second_call = service.GetDatabasePassword();

            // Assert
            password_second_call.Should().Be(password_first_call);
        }
    }
}
