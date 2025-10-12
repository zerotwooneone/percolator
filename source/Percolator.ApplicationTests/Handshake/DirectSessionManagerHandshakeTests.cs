using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Handshake
{
    [TestFixture]
    public class DirectSessionManagerHandshakeTests
    {
        private sealed class FakeSessionStore : IDoubleRatchetSessionStore
        {
            private readonly Dictionary<(int self, Guid sid), DoubleRatchetSession.DoubleRatchetSessionState> _states = new();

            public Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId, int selfIdentityId)
            {
                _states.TryGetValue((selfIdentityId, sessionId.Value), out var state);
                return Task.FromResult<DoubleRatchetSession.DoubleRatchetSessionState?>(state);
            }

            public Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState state, int selfIdentityId)
            {
                _states[(selfIdentityId, sessionId.Value)] = state;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SessionId>> GetAllSessionIdsAsync(int selfIdentityId)
            {
                var ids = _states.Keys.Where(k => k.self == selfIdentityId).Select(k => new SessionId(k.sid)).ToArray();
                return Task.FromResult((IReadOnlyList<SessionId>)ids);
            }

            public Task<DoubleRatchetSession.DoubleRatchetSessionState?> FindByRemoteRatchetKeyAsync(PreKey remoteRatchetKey, int selfIdentityId)
            {
                var match = _states.Where(kv => kv.Key.self == selfIdentityId && kv.Value.TheirDhRatchetPublicKey is not null && kv.Value.TheirDhRatchetPublicKey.Value.SequenceEqual(remoteRatchetKey.Value))
                    .Select(kv => kv.Value).FirstOrDefault();
                return Task.FromResult<DoubleRatchetSession.DoubleRatchetSessionState?>(match);
            }
        }

        private sealed class FakeRatchetLookup : IRatchetKeySessionLookup
        {
            public Task<Percolator.Network.DirectSessionId?> TryResolveAsync(PreKey ratchetPublicKey, int selfIdentityId, CancellationToken ct)
                => Task.FromResult<Percolator.Network.DirectSessionId?>(null);

            public Task UpsertAsync(Percolator.Network.DirectSessionId sessionId, int selfIdentityId, PreKey ratchetPublicKey, DateTimeOffset updatedAtUtc, CancellationToken ct)
                => Task.CompletedTask;
        }

        private sealed class CapturingPreHandshakeStore : IPreHandshakeSessionStore
        {
            public PreHandshakeRecord? LastSaved;
            public long? LastDeletedId;
            public int DeleteCalls;

            public Task SaveAsync(PreHandshakeRecord record, CancellationToken cancellationToken)
            {
                LastSaved = record;
                return Task.CompletedTask;
            }

            public async IAsyncEnumerable<PreHandshakeRecord> EnumeratePendingAsync(int selfIdentityId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (LastSaved is not null && LastSaved.SelfIdentityId == selfIdentityId)
                {
                    yield return LastSaved!;
                }
                await Task.CompletedTask;
            }

            public Task DeleteAsync(long recordId, int selfIdentityId, CancellationToken cancellationToken)
            {
                LastDeletedId = recordId;
                DeleteCalls++;
                return Task.CompletedTask;
            }
            public Task PurgeExpiredAsync(int selfIdentityId, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Test]
        public async Task Alice_Initiates_Persists_Minimal_PreHandshakeRecord()
        {
            var sessionStore = new FakeSessionStore();
            var preHandshake = new CapturingPreHandshakeStore();
            var ratchetLookup = new FakeRatchetLookup();

            var active = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice-Test") { SelfIdentityId = 1 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };
            var loggerFactory = LoggerFactory.Create(b => { });
            var logger = loggerFactory.CreateLogger<DirectSessionManager>();
            var cryptoOptions = Options.Create(new CryptographyOptions());

            var dsm = new DirectSessionManager(sessionStore, active, logger, loggerFactory, cryptoOptions, ratchetLookup, preHandshake);

            using var initEph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var initEphPub = initEph.PublicKey.ExportSubjectPublicKeyInfo();
            var remoteIdKey = new RatchetIdentityKey(initEph.PublicKey.ExportSubjectPublicKeyInfo());
            var remotePreKey = new PreKey(initEph.PublicKey.ExportSubjectPublicKeyInfo());
            var shared = new SharedSecret(new byte[32]);

            var recipientPkh = new byte[] { 0x01 };
            var first = await dsm.EstablishSessionAsInitiatorAsync(
                recipientPkh,
                signedPreKeyId: Guid.Empty,
                oneTimePreKeyId: null,
                remoteIdentityKey: remoteIdKey,
                remotePreKey: remotePreKey,
                sharedSecret: shared,
                initiatorEphemeral: initEph,
                initialPlaintext: null,
                cancellationToken: CancellationToken.None);

            Assert.That(first, Is.Null, "No initial plaintext provided so first message should be null");
            Assert.That(preHandshake.LastSaved, Is.Not.Null);
            Assert.That(preHandshake.LastSaved?.InitialRootKey, Is.Not.Null);
            Assert.That(preHandshake.LastSaved?.InitiatorEphemeralPrivateKey, Is.Not.Null);
            Assert.That(preHandshake.LastSaved?.RecipientPublicKeyHash, Is.EqualTo(recipientPkh));
            Assert.That(preHandshake.LastSaved?.SelfIdentityId, Is.EqualTo(1));
        }

        [Test]
        public async Task Alice_Initiates_With_Initial_Message_Bob_Completes_And_Decrypts_First_Message()
        {
            var aliceStore = new FakeSessionStore();
            var bobStore = new FakeSessionStore();
            var preHandshake = new CapturingPreHandshakeStore();
            var ratchetLookup = new FakeRatchetLookup();

            // Alice identity context
            var aliceActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 1001 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };

            // Bob identity context
            var bobActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Bob") { SelfIdentityId = 2002 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };

            var loggerFactory = LoggerFactory.Create(b => { });
            var options = Options.Create(new CryptographyOptions());

            var aliceDsm = new DirectSessionManager(aliceStore, aliceActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup, preHandshake);
            var bobDsm = new DirectSessionManager(bobStore, bobActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup, preHandshake);

            using var initEph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var initEphPub = initEph.PublicKey.ExportSubjectPublicKeyInfo();
            using var bobSpk = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

            // Alice encrypts to Bob: use Bob's identity pubkey and Bob's signed-pre-key pubkey
            var remoteIdKey = new RatchetIdentityKey(bobActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var remotePreKey = new PreKey(bobSpk.PublicKey.ExportSubjectPublicKeyInfo());
            var shared = new SharedSecret(new byte[32]);

            // Alice initiates pre-handshake with initial message
            var initialPlaintext = new Plaintext(new byte[] { 0xDE, 0xAD });
            var first = await aliceDsm.EstablishSessionAsInitiatorAsync(
                recipientPublicKeyHash: new byte[] { 0x01 },
                signedPreKeyId: Guid.Empty,
                oneTimePreKeyId: null,
                remoteIdentityKey: remoteIdKey,
                remotePreKey: remotePreKey,
                sharedSecret: shared,
                initiatorEphemeral: initEph,
                initialPlaintext: initialPlaintext,
                cancellationToken: CancellationToken.None);

            Assert.That(first, Is.Not.Null);

            // Bob establishes responder session with a chosen session id
            var sessionId = new SessionId(Guid.NewGuid());
            // For responder, remoteIdentityKey should be Alice's identity pubkey, and remotePreKey should be Alice's ephemeral pubkey
            var responderRemoteId = new RatchetIdentityKey(aliceActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var responderRemotePreKey = new PreKey(initEphPub);
            await bobDsm.EstablishSessionAsResponderAsync(sessionId, remoteIdentityKey: responderRemoteId, remotePreKey: responderRemotePreKey, privateKeyUsedInHandshake: bobSpk, sharedSecret: shared);

            // Bob tries to infer and receive without knowing sid
            var inferred = await bobDsm.TryInferAndReceiveAsync(first!, CancellationToken.None);
            Assert.That(inferred, Is.Not.Null);
            Assert.That(inferred?.plaintext, Is.Not.Null);
            Assert.That(inferred?.plaintext!.Value, Is.EqualTo(initialPlaintext.Value));
        }

        [Test]
        public void Alice_Completes_Handshake_From_Responder_Hello_And_Upserts_RatchetIndex()
        {
            // Arrange
            var aliceStore = new FakeSessionStore();
            var preHandshake = new CapturingPreHandshakeStore();
            var ratchetLookup = new Moq.Mock<IRatchetKeySessionLookup>(Moq.MockBehavior.Strict);

            // Expect one upsert
            ratchetLookup
                .Setup(x => x.UpsertAsync(Moq.It.IsAny<Percolator.Network.DirectSessionId>(), Moq.It.IsAny<int>(), Moq.It.IsAny<PreKey>(), Moq.It.IsAny<DateTimeOffset>(), Moq.It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();
            // Allow resolver to be invoked and return null for slow-path finalize
            ratchetLookup
                .Setup(x => x.TryResolveAsync(Moq.It.IsAny<PreKey>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<Percolator.Network.DirectSessionId?>(null));

            var aliceActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 3003 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };

            var loggerFactory = LoggerFactory.Create(b => { });
            var options = Options.Create(new CryptographyOptions());
            var aliceDsm = new DirectSessionManager(aliceStore, aliceActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup.Object, preHandshake);

            // Define Bob identity and signed-pre-key for initiator params
            var bobActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Bob") { SelfIdentityId = 4004 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };
            using var bobSpk = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var initEph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var initEphPubFinalize = initEph.PublicKey.ExportSubjectPublicKeyInfo();
            // Alice encrypts to Bob: use Bob's identity pubkey and Bob's signed-pre-key pubkey
            var remoteIdKey = new RatchetIdentityKey(bobActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var remotePreKey = new PreKey(bobSpk.PublicKey.ExportSubjectPublicKeyInfo());
            var shared = new SharedSecret(new byte[32]);

            // Alice saves pre-handshake (no initial plaintext)
            var first = aliceDsm.EstablishSessionAsInitiatorAsync(
                recipientPublicKeyHash: new byte[] { 0x77 },
                signedPreKeyId: Guid.Empty,
                oneTimePreKeyId: null,
                remoteIdentityKey: remoteIdKey,
                remotePreKey: remotePreKey,
                sharedSecret: shared,
                initiatorEphemeral: initEph,
                initialPlaintext: null,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            // Bob side: create responder session to produce the responder hello message
            var bobStore = new FakeSessionStore();
            var bobDsm = new DirectSessionManager(bobStore, bobActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup.Object, preHandshake);
            var sessionId = new SessionId(Guid.NewGuid());
            var responderRemoteId2 = new RatchetIdentityKey(aliceActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var responderRemotePreKey2 = new PreKey(initEphPubFinalize);
            bobDsm.EstablishSessionAsResponderAsync(sessionId, remoteIdentityKey: responderRemoteId2, remotePreKey: responderRemotePreKey2, privateKeyUsedInHandshake: bobSpk, sharedSecret: shared).GetAwaiter().GetResult();

            // Bob encrypts a plaintext that encodes sessionId, to serve as responder hello
            var sidBytes = sessionId.Value.ToByteArray();
            var responderHello = bobDsm.EncryptMessageAsync(sessionId, new Plaintext(sidBytes)).GetAwaiter().GetResult();

            // Envelope helpers
            (SessionId SessionId, Plaintext Pt) getEnv(Plaintext pt) => (new SessionId(new Guid(pt.Value)), pt);
            SessionId getSid((SessionId SessionId, Plaintext Pt) env) => env.SessionId;

            // Act
            var result = aliceDsm.CompleteHandshakeAsync(responderHello, getEnv, getSid, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            Assert.That(result.sessionId.Value, Is.EqualTo(sessionId.Value));
            Assert.That(preHandshake.LastDeletedId, Is.Not.Null);
            Assert.That(preHandshake.DeleteCalls, Is.EqualTo(1));
            ratchetLookup.Verify();
        }

        [Test]
        public async Task Both_Sides_Can_Send_And_Receive_Messages_After_Handshake()
        {
            // Arrange: create DSMs and pre-handshake
            var preHandshake = new CapturingPreHandshakeStore();
            var ratchetLookup = new FakeRatchetLookup();
            var loggerFactory = LoggerFactory.Create(b => { });
            var options = Options.Create(new CryptographyOptions());

            var aliceActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 5005 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };
            var bobActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Bob") { SelfIdentityId = 6006 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };

            var aliceStore = new FakeSessionStore();
            var bobStore = new FakeSessionStore();
            var aliceDsm = new DirectSessionManager(aliceStore, aliceActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup, preHandshake);
            var bobDsm = new DirectSessionManager(bobStore, bobActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup, preHandshake);

            using var initEph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var initEphPub2 = initEph.PublicKey.ExportSubjectPublicKeyInfo();
            using var bobSpk = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            // Alice encrypts to Bob: use Bob's identity pubkey and Bob's signed-pre-key pubkey
            var remoteIdKey = new RatchetIdentityKey(bobActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var remotePreKey = new PreKey(bobSpk.PublicKey.ExportSubjectPublicKeyInfo());
            var shared = new SharedSecret(new byte[32]);

            var first = await aliceDsm.EstablishSessionAsInitiatorAsync(new byte[]{0x01}, Guid.Empty, null, remoteIdKey, remotePreKey, shared, initEph, new Plaintext(new byte[]{0xAA}), CancellationToken.None);
            Assert.That(first, Is.Not.Null);

            // Bob establishes responder session before trying to infer/decrypt Alice's first message
            var sessionId = new SessionId(Guid.NewGuid());
            var responderRemoteId3 = new RatchetIdentityKey(aliceActive.Keys!.IdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo());
            var responderRemotePreKey3 = new PreKey(initEphPub2);
            await bobDsm.EstablishSessionAsResponderAsync(sessionId, responderRemoteId3, responderRemotePreKey3, bobSpk, shared);

            var inferred = await bobDsm.TryInferAndReceiveAsync(first!, CancellationToken.None);
            Assert.That(inferred?.plaintext, Is.Not.Null);

            var sidBytes = sessionId.Value.ToByteArray();
            var responderHello = await bobDsm.EncryptMessageAsync(sessionId, new Plaintext(sidBytes));
            (SessionId SessionId, Plaintext Pt) getEnv(Plaintext pt) => (new SessionId(new Guid(pt.Value)), pt);
            SessionId getSid((SessionId SessionId, Plaintext Pt) env) => env.SessionId;
            var finalizeResult = await aliceDsm.CompleteHandshakeAsync(responderHello, getEnv, getSid, CancellationToken.None);
            var establishedSid = finalizeResult.sessionId;

            // Act: Bob sends to Alice
            var m1 = await bobDsm.EncryptMessageAsync(establishedSid, new Plaintext(new byte[]{0xB1, 0xB2}));
            var ptAfter = await aliceDsm.ReceiveMessageAsync(establishedSid, m1);
            Assert.That(ptAfter!.Value, Is.EqualTo(new byte[]{0xB1,0xB2}));

            // Act: Alice replies
            var m2 = await aliceDsm.EncryptMessageAsync(establishedSid, new Plaintext(new byte[]{0xC3}));
            var ptBob = await bobDsm.ReceiveMessageAsync(establishedSid, m2);
            Assert.That(ptBob!.Value, Is.EqualTo(new byte[]{0xC3}));
        }

        [Test]
        public void Missing_Or_Expired_PreHandshakeRecord_Causes_SlowPath_Failure()
        {
            // Arrange: Alice has no pending records
            var emptyStore = new CapturingPreHandshakeStore();
            var ratchetLookup = new FakeRatchetLookup();
            var loggerFactory = LoggerFactory.Create(b => { });
            var options = Options.Create(new CryptographyOptions());
            var aliceActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 7007 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };
            var aliceStore = new FakeSessionStore();
            var dsm = new DirectSessionManager(aliceStore, aliceActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup, emptyStore);

            // A random message that won't decrypt with any state
            var bogus = new SessionRatchetMessage(new byte[]{1,2,3,4,5,6});
            Assert.Catch<Exception>(() => dsm.CompleteHandshakeAsync(bogus, pt => (new SessionId(Guid.NewGuid()), pt), env => env.Item1, CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void Corrupted_Responder_Hello_Does_Not_Upsert_RatchetIndex()
        {
            // Arrange
            var preHandshake = new CapturingPreHandshakeStore();
            var ratchetLookup = new Moq.Mock<IRatchetKeySessionLookup>(Moq.MockBehavior.Strict);
            // No upsert should occur
            var loggerFactory = LoggerFactory.Create(b => { });
            var options = Options.Create(new CryptographyOptions());
            var aliceActive = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Alice") { SelfIdentityId = 8008 },
                Keys = new Percolator.Identity.X3dhKeys(
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                    System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            };
            var aliceStore = new FakeSessionStore();
            var dsm = new DirectSessionManager(aliceStore, aliceActive, loggerFactory.CreateLogger<DirectSessionManager>(), loggerFactory, options, ratchetLookup.Object, preHandshake);

            // Save a pending record
            using var eph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var remoteIdKey = new RatchetIdentityKey(eph.PublicKey.ExportSubjectPublicKeyInfo());
            var remotePreKey = new PreKey(eph.PublicKey.ExportSubjectPublicKeyInfo());
            var shared = new SharedSecret(new byte[32]);
            dsm.EstablishSessionAsInitiatorAsync(new byte[]{0x9A}, Guid.Empty, null, remoteIdKey, remotePreKey, shared, eph, null, CancellationToken.None).GetAwaiter().GetResult();

            // Corrupt message payload so decrypt fails for all candidates
            var corrupted = new SessionRatchetMessage(new byte[]{0xFF,0xEE,0xDD});
            Assert.Catch<Exception>(() => dsm.CompleteHandshakeAsync(corrupted, pt => (new SessionId(Guid.NewGuid()), pt), env => env.Item1, CancellationToken.None).GetAwaiter().GetResult());
            ratchetLookup.VerifyNoOtherCalls();
        }
    }
}
