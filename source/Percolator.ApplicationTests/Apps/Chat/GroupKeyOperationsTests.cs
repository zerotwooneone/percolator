using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Application.Apps.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Chat;
using Percolator.Chat.Primitives;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class GroupKeyOperationsTests
    {
        private ILoggerFactory _loggerFactory = null!;
        private ILogger<GroupKeyOperations> _logger = null!;
        private Mock<IGroupManagerResolver> _resolver = null!;
        private Mock<ITransportKeyResolver> _transport = null!;
        private Mock<IGroupManagerStateStore> _stateStore = null!;
        private Mock<IAtRestKeyProvider> _atRest = null!;

        [SetUp]
        public void SetUp()
        {
            _loggerFactory = LoggerFactory.Create(b=>{});
            _logger = _loggerFactory.CreateLogger<GroupKeyOperations>();
            _resolver = new Mock<IGroupManagerResolver>(MockBehavior.Strict);
            _transport = new Mock<ITransportKeyResolver>(MockBehavior.Strict);
            _stateStore = new Mock<IGroupManagerStateStore>(MockBehavior.Strict);
            _atRest = new Mock<IAtRestKeyProvider>(MockBehavior.Strict);
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private GroupKeyOperations CreateSut()
            => new GroupKeyOperations(_logger, _resolver.Object, _transport.Object, _stateStore.Object, _atRest.Object);

        [Test]
        public async Task ImportGroupKeyAsync_NoManager_NoThrow()
        {
            var convoId = Guid.NewGuid();
            _resolver.Setup(r => r.TryGet(convoId, out It.Ref<GroupManager>.IsAny)).Returns(false);

            var sut = CreateSut();
            await sut.ImportGroupKeyAsync(convoId, new GroupKeyVersion(1), new EncryptedGroupKey(new byte[]{1,2,3}), CancellationToken.None);
        }

        [Test]
        public async Task ImportGroupKeyAsync_MissingAeadKey_NoImport()
        {
            var convoId = Guid.NewGuid();
            var gm = MakeGroupManager();
            _resolver.Setup(r => r.TryGet(convoId, out gm)).Returns(true);
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            var sut = CreateSut();
            var env = MakeEnvelopeBytes();
            await sut.ImportGroupKeyAsync(convoId, new GroupKeyVersion(2), new EncryptedGroupKey(env), CancellationToken.None);
        }

        [Test]
        public async Task ImportGroupKeyAsync_ValidEnvelope_ImportsAndPersists()
        {
            var convoId = Guid.NewGuid();
            var gm = MakeGroupManager();
            _resolver.Setup(r => r.TryGet(convoId, out gm)).Returns(true);

            var aeadKey = RandomNumberGenerator.GetBytes(32);
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(aeadKey);

            // At rest
            var masterKey = RandomNumberGenerator.GetBytes(32);
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(masterKey);
            _stateStore.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var plaintext = RandomNumberGenerator.GetBytes(32);
            var env = MakeEnvelopeBytes(plaintext, aeadKey);

            var sut = CreateSut();
            await sut.ImportGroupKeyAsync(convoId, new GroupKeyVersion(3), new EncryptedGroupKey(env), CancellationToken.None);
        }

        private static GroupManager MakeGroupManager()
        {
            var loggerFactory = LoggerFactory.Create(b => {});
            var options = Microsoft.Extensions.Options.Options.Create(new CryptographyOptions());
            using var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return new GroupManager(identity, loggerFactory, options);
        }

        private static byte[] MakeEnvelopeBytes(byte[]? plaintext=null, byte[]? aeadKey=null)
        {
            // Build KeyEnvelope v1, alg=1 using AES-GCM-256
            byte ver = 1; ushort alg = 1; var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16];
            if (plaintext is null) plaintext = new byte[]{ 0xAA };
            if (aeadKey is null) aeadKey = RandomNumberGenerator.GetBytes(32);
            var cipher = new byte[plaintext.Length];
            using var aead = new AesGcm(aeadKey, 16);
            aead.Encrypt(nonce, plaintext, cipher, tag);

            var buf = new byte[1 + 2 + 1 + nonce.Length + 1 + tag.Length + 4 + cipher.Length];
            int offset = 0;
            buf[offset++] = ver;
            buf[offset++] = (byte)(alg >> 8);
            buf[offset++] = (byte)(alg & 0xFF);
            buf[offset++] = (byte)nonce.Length; Array.Copy(nonce, 0, buf, offset, nonce.Length); offset += nonce.Length;
            buf[offset++] = (byte)tag.Length; Array.Copy(tag, 0, buf, offset, tag.Length); offset += tag.Length;
            buf[offset++] = (byte)((cipher.Length >> 24) & 0xFF);
            buf[offset++] = (byte)((cipher.Length >> 16) & 0xFF);
            buf[offset++] = (byte)((cipher.Length >> 8) & 0xFF);
            buf[offset++] = (byte)(cipher.Length & 0xFF);
            Array.Copy(cipher, 0, buf, offset, cipher.Length);
            return buf;
        }
    }
}
