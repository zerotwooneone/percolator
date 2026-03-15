using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class GroupSenderKeyServiceTests
    {
        [Test]
        public async Task ImportSenderKeyAsync_WhenNotExists_DecryptsAndPersists()
        {
            // Arrange
            var repo = new Mock<IGroupSenderKeyRepository>(MockBehavior.Loose);
            var crypto = new Mock<IEnvelopeCrypto>(MockBehavior.Loose);
            var service = new GroupSenderKeyService(repo.Object, crypto.Object);
            var convoId = Guid.NewGuid();
            var version = new GroupKeyVersion(3);
            var cipher = new EncryptedGroupKey(new byte[] { 0x01, 0x02 });

            repo.Setup(r => r.ExistsAsync(convoId, version, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            crypto.Setup(c => c.DecryptAsync(convoId, cipher, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new byte[] { 0xAA }, new byte[] { 0xBB }));

            // Act
            await service.ImportSenderKeyAsync(convoId, version, cipher, default);

            // Assert (side-effect): persisted keys
            repo.Verify(r => r.UpsertAsync(
                convoId,
                version,
                It.Is<byte[]>(b => b.Length == 1 && b[0] == 0xAA),
                It.Is<byte[]>(b => b.Length == 1 && b[0] == 0xBB),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void ImportSenderKeyAsync_WhenDecryptFails_Throws()
        {
            // Arrange
            var repo = new Mock<IGroupSenderKeyRepository>(MockBehavior.Loose);
            var crypto = new Mock<IEnvelopeCrypto>(MockBehavior.Loose);
            var service = new GroupSenderKeyService(repo.Object, crypto.Object);
            var convoId = Guid.NewGuid();
            var version = new GroupKeyVersion(1);
            var cipher = new EncryptedGroupKey(Array.Empty<byte>());

            repo.Setup(r => r.ExistsAsync(convoId, version, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            crypto.Setup(c => c.DecryptAsync(convoId, cipher, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("bad envelope"));

            // Act & Assert
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.ImportSenderKeyAsync(convoId, version, cipher, default));
        }

        [Test]
        public async Task ImportSenderKeyAsync_WhenAlreadyExists_IsIdempotent()
        {
            // Arrange
            var repo = new Mock<IGroupSenderKeyRepository>(MockBehavior.Loose);
            var crypto = new Mock<IEnvelopeCrypto>(MockBehavior.Loose);
            var service = new GroupSenderKeyService(repo.Object, crypto.Object);
            var convoId = Guid.NewGuid();
            var version = new GroupKeyVersion(5);
            var cipher = new EncryptedGroupKey(new byte[] { 0x10 });

            repo.Setup(r => r.ExistsAsync(convoId, version, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Act
            await service.ImportSenderKeyAsync(convoId, version, cipher, default);

            // Assert (idempotent): no decrypt or upsert when already exists
            crypto.Verify(c => c.DecryptAsync(It.IsAny<Guid>(), It.IsAny<EncryptedGroupKey>(), It.IsAny<CancellationToken>()), Times.Never);
            repo.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<GroupKeyVersion>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
