using Moq;
using Percolator.Application.Network.Handshake;
using Percolator.Network;

namespace Percolator.ApplicationTests.TestHelpers
{
    public static class AsyncSeq
    {
        public static async IAsyncEnumerable<T> Empty<T>()
        {
            yield break;
        }
    }

    public sealed class PreHandshakeRecordBuilder
    {
        private int _selfId = 1;
        private byte[] _pkh = new byte[] { 0x01 };
        private Guid _req = Guid.NewGuid();
        private byte[] _epriv = Array.Empty<byte>();
        private byte[] _irk = new byte[] { 0xAA };
        private DateTimeOffset _created = DateTimeOffset.UtcNow;
        private DateTimeOffset _expires = DateTimeOffset.UtcNow.AddMinutes(5);
        private byte[] _remoteSpki = new byte[] { 0x10 };
        private int _id = 1000;

        public PreHandshakeRecordBuilder WithIds(int selfId, int id)
        {
            _selfId = selfId; _id = id; return this;
        }
        public PreHandshakeRecordBuilder WithHashes(byte[] pkh)
        { _pkh = pkh; return this; }
        public PreHandshakeRecordBuilder WithInitialRootKey(byte[] irk)
        { _irk = irk; return this; }
        public PreHandshakeRecordBuilder WithRemoteSpki(byte[] spki)
        { _remoteSpki = spki; return this; }
        public PreHandshakeRecordBuilder WithExpiry(DateTimeOffset created, DateTimeOffset expires)
        { _created = created; _expires = expires; return this; }

        public PreHandshakeRecord Build()
        {
            return new PreHandshakeRecord(
                Id: _id,
                SelfIdentityId: _selfId,
                RecipientPublicKeyHash: _pkh,
                LocalRequestId: _req,
                InitiatorEphemeralPrivateKey: _epriv,
                InitialRootKey: _irk,
                CreatedAtUtc: _created,
                ExpiresAtUtc: _expires,
                RemoteIdentityKeySpki: _remoteSpki);
        }
    }

    public static class PreHandshakeStoreFixture
    {
        public static void SetupEmptyEnumerate(Mock<IPreHandshakeSessionStore> store)
        {
            store.Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .Returns((int _, CancellationToken __) => AsyncSeq.Empty<PreHandshakeRecord>());
        }

        public static void SetupMostRecentAndDelete(Mock<IPreHandshakeSessionStore> store, int selfId, PreHandshakeRecord record)
        {
            store.Setup(s => s.TryGetMostRecentAsync(selfId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(record);
            store.Setup(s => s.DeleteAsync(record.Id, selfId, It.IsAny<CancellationToken>()))
                 .Returns(System.Threading.Tasks.Task.CompletedTask);
        }
    }

    public static class ProfileRepoFixture
    {
        public static void SetupNoExisting(Mock<IPeerRoutingProfileRepository> repo)
        {
            repo.Setup(r => r.GetByIdAsync(It.IsAny<PeerId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerRoutingProfile?)null);
            repo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
                .Returns(System.Threading.Tasks.Task.CompletedTask);
        }

        public static void SetupExisting(Mock<IPeerRoutingProfileRepository> repo, PeerRoutingProfile existing)
        {
            repo.Setup(r => r.GetByIdAsync(It.IsAny<PeerId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(existing);
            repo.Setup(r => r.UpsertAsync(existing, It.IsAny<CancellationToken>()))
                .Returns(System.Threading.Tasks.Task.CompletedTask);
        }
    }
}
