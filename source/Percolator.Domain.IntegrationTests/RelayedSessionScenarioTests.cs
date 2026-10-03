using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class RelayedSessionScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;
    private IPrivatePreKeyStore _bobPreKeyStore = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
        _bobPreKeyStore = new InMemoryPrivatePreKeyStore();
    }

    [Test]
    public async Task RelayedSession_PreKeyDirectory_AtomicConsumption_And_AsymmetricChains()
    {
        // -------------------------------------------------------------------------
        // 1. BOB INITIALIZES IDENTITY, SIGNED PREKEY, AND OPK POOL
        // -------------------------------------------------------------------------
        var bobId = PublicIdentityId.New();
        var bobDeviceId = DeviceId.Primary;
        var (bobIdentityPriv, bobIdentityPub) = _engine.GenerateIdentityKeyPair();

        var (bobSpkPriv, bobSpkPub) = _engine.GenerateEphemeralKeyPair();
        var bobSpkSignature = _engine.SignEd25519(bobIdentityPriv.Span, bobSpkPub.Span);

        // Bob generates 3 One-Time Pre-Keys (OPKs) and stores their private keys in his secure store
        var (opk101Priv, opk101Pub) = _engine.GenerateEphemeralKeyPair();
        var (opk102Priv, opk102Pub) = _engine.GenerateEphemeralKeyPair();
        var (opk103Priv, opk103Pub) = _engine.GenerateEphemeralKeyPair();

        await _bobPreKeyStore.StoreSignedPreKeyPrivateAsync(bobId, bobDeviceId, bobSpkPriv);
        await _bobPreKeyStore.StoreOneTimePreKeysPrivateAsync(bobId, bobDeviceId, new[]
        {
            (101u, opk101Priv),
            (102u, opk102Priv),
            (103u, opk103Priv)
        });

        // -------------------------------------------------------------------------
        // 2. SIMULATED RELAY DIRECTORY HOSTS BOB'S BUNDLE AGGREGATE
        // -------------------------------------------------------------------------
        var hostedBundleResult = RelayHostedPreKeyBundle.Create(
            bobId,
            bobDeviceId,
            bobIdentityPub,
            bobSpkPub,
            DeviceLinkProof.FromSpan(bobSpkSignature),
            new[] { (101u, opk101Pub), (102u, opk102Pub), (103u, opk103Pub) },
            RelayHostingPolicy.Default,
            _engine);

        hostedBundleResult.IsSuccess.Should().BeTrue();
        var hostedBundle = hostedBundleResult.Value;
        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(3);

        // Alice queries the relay directory for Bob's active pre-key bundle
        var consumeResult = hostedBundle.ConsumeBundle();
        consumeResult.IsSuccess.Should().BeTrue();
        var bobConsumedBundle = consumeResult.Value;

        bobConsumedBundle.OneTimePreKey.Should().NotBeNull();
        bobConsumedBundle.OneTimePreKeyId.Should().Be(101u);
        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(2, "Relay dequeues the OPK upon dispensing.");

        // -------------------------------------------------------------------------
        // 3. ALICE PERFORMS 4-DH X3DH INITIATION
        // -------------------------------------------------------------------------
        var aliceId = PublicIdentityId.New();
        var aliceDeviceId = DeviceId.Primary;
        var (aliceIdentityPriv, aliceIdentityPub) = _engine.GenerateIdentityKeyPair();

        var aliceX3dhResult = X3dhAgreement.Initiate(
            aliceIdentityPriv.Span,
            aliceIdentityPub,
            bobConsumedBundle,
            _engine);

        aliceX3dhResult.IsSuccess.Should().BeTrue();
        aliceX3dhResult.Value.OneTimePreKeyIdUsed.Should().Be(101u);

        var aliceSessionResult = DirectRatchetSession.CreateFromX3dhInitiator(
            aliceId,
            aliceDeviceId,
            bobId,
            bobDeviceId,
            aliceX3dhResult.Value,
            bobSpkPub,
            _engine);

        aliceSessionResult.IsSuccess.Should().BeTrue();
        using var aliceSession = aliceSessionResult.Value;

        // Alice encrypts an initial message to be dispatched via relay
        var initialPlaintext = "Hello Bob! Initiating relayed 1:1 session via OPK-101.";
        var stepAlice = aliceSession.StepSendingChain(_engine);
        stepAlice.IsSuccess.Should().BeTrue();

        var (aliceCounter, aliceMsgKey, aliceEphemKey) = stepAlice.Value;
        var msgNonce = new byte[12];
        BitConverter.GetBytes((ulong)aliceCounter).CopyTo(msgNonce, 0);

        var ciphertext = _engine.EncryptAesGcm(
            aliceMsgKey.Span,
            msgNonce,
            Encoding.UTF8.GetBytes(initialPlaintext),
            associatedData: Encoding.UTF8.GetBytes($"relay-header-{aliceId}"));

        // -------------------------------------------------------------------------
        // 4. BOB RECEIVES RELAYED ENVELOPE AND ATOMICALLY CONSUMES OPK
        // -------------------------------------------------------------------------
        // Bob inspects the wire envelope: it references OPK ID 101
        var consumedOpkPriv = await _bobPreKeyStore.TryConsumeOneTimePreKeyPrivateAsync(bobId, bobDeviceId, 101u);
        consumedOpkPriv.Should().NotBeNull();

        // Bob performs 4-DH X3DH responder agreement
        var bobX3dhResult = X3dhAgreement.Receive(
            bobIdentityPriv.Span,
            bobSpkPriv.Span,
            consumedOpkPriv!.Span,
            aliceIdentityPub,
            aliceX3dhResult.Value.EphemeralPublicKey,
            _engine);

        bobX3dhResult.IsSuccess.Should().BeTrue();
        var bobMasterSecret = bobX3dhResult.Value;

        // Master secrets derived by Alice and Bob must be identical
        aliceX3dhResult.Value.MasterSecret.Span.ToArray().Should().BeEquivalentTo(bobMasterSecret.Span.ToArray());

        // Bob creates inbound session
        var bobSessionResult = DirectRatchetSession.CreateFromX3dhResponder(
            bobId,
            bobDeviceId,
            aliceId,
            aliceDeviceId,
            bobMasterSecret,
            aliceX3dhResult.Value.EphemeralPublicKey,
            _engine);

        bobSessionResult.IsSuccess.Should().BeTrue();
        using var bobSession = bobSessionResult.Value;

        // Bob decrypts Alice's initial message
        var stepBob = bobSession.StepReceivingChain(_engine, targetCounter: aliceCounter);
        stepBob.IsSuccess.Should().BeTrue();
        using var bobDecryptionKey = stepBob.Value.Key;

        var decryptedBytes = _engine.DecryptAesGcm(
            bobDecryptionKey.Span,
            msgNonce,
            ciphertext,
            associatedData: Encoding.UTF8.GetBytes($"relay-header-{aliceId}"));

        Encoding.UTF8.GetString(decryptedBytes).Should().Be(initialPlaintext);

        // -------------------------------------------------------------------------
        // 5. VERIFY SINGLE-USE OF ONE-TIME PRE-KEY (REPLAY PREVENTION)
        // -------------------------------------------------------------------------
        // A malicious relay replay or second inbound session with OPK 101 must fail
        var secondConsumption = await _bobPreKeyStore.TryConsumeOneTimePreKeyPrivateAsync(bobId, bobDeviceId, 101u);
        secondConsumption.Should().BeNull("One-time prekeys must be strictly consumed once to guarantee forward secrecy.");

        // -------------------------------------------------------------------------
        // 6. BOB RESPONDS OVER RELAY (SYMMETRIC TO ASYMMETRIC DH RATCHET TRANSITION)
        // -------------------------------------------------------------------------
        var replyPlaintext = "Acknowledged Alice. OPK-101 consumed and destroyed. Session secure.";
        var bobSendStep = bobSession.StepSendingChain(_engine);
        bobSendStep.IsSuccess.Should().BeTrue();

        var (bobCounter, bobMsgKey, bobEphemKey) = bobSendStep.Value;
        bobEphemKey.Should().NotBeNull("Bob's reply must generate a new ratchet ephemeral key.");

        var replyNonce = new byte[12];
        BitConverter.GetBytes((ulong)bobCounter).CopyTo(replyNonce, 0);

        var replyCiphertext = _engine.EncryptAesGcm(
            bobMsgKey.Span,
            replyNonce,
            Encoding.UTF8.GetBytes(replyPlaintext),
            associatedData: Encoding.UTF8.GetBytes($"relay-header-{bobId}"));

        // Alice receives Bob's relayed reply, performs DH ratchet step, and decrypts
        var aliceRatchet = aliceSession.StepDhRatchet(bobEphemKey!, _engine);
        aliceRatchet.IsSuccess.Should().BeTrue();

        var aliceRecvStep = aliceSession.StepReceivingChain(_engine, targetCounter: bobCounter);
        aliceRecvStep.IsSuccess.Should().BeTrue();
        using var aliceDecryptionKey = aliceRecvStep.Value.Key;

        var decryptedReplyBytes = _engine.DecryptAesGcm(
            aliceDecryptionKey.Span,
            replyNonce,
            replyCiphertext,
            associatedData: Encoding.UTF8.GetBytes($"relay-header-{bobId}"));

        Encoding.UTF8.GetString(decryptedReplyBytes).Should().Be(replyPlaintext);
    }
}
