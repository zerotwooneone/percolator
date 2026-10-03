using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class DirectSessionScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
    }

    [Test]
    public void DirectSession_RealWorld_FullLifecycle_BidirectionalRatcheting_And_OutOfOrderDelivery()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP IDENTITIES & KEYS (Alice and Bob)
        // -------------------------------------------------------------------------
        var aliceId = PublicIdentityId.New();
        var aliceDeviceId = DeviceId.Primary;
        var (aliceIdentityPriv, aliceIdentityPub) = _engine.GenerateIdentityKeyPair();

        var bobId = PublicIdentityId.New();
        var bobDeviceId = DeviceId.Primary;
        var (bobIdentityPriv, bobIdentityPub) = _engine.GenerateIdentityKeyPair();

        // Bob publishes his signed pre-key
        var (bobSpkPriv, bobSpkPub) = _engine.GenerateEphemeralKeyPair();
        var bobSpkSignature = _engine.SignEd25519(bobIdentityPriv.Span, bobSpkPub.Span);

        var bobBundle = new PreKeyBundle(
            bobId,
            bobDeviceId,
            bobIdentityPub,
            bobSpkPub,
            DeviceLinkProof.FromSpan(bobSpkSignature));

        // -------------------------------------------------------------------------
        // 2. MUTUAL X3DH NEGOTIATION
        // -------------------------------------------------------------------------
        // Alice initiates X3DH against Bob's bundle
        var aliceX3dh = X3dhAgreement.Initiate(
            aliceIdentityPriv.Span,
            aliceIdentityPub,
            bobBundle,
            _engine);
        aliceX3dh.IsSuccess.Should().BeTrue();
        var aliceResult = aliceX3dh.Value;

        // Alice establishes her outbound Double Ratchet session
        var aliceSessionResult = DirectRatchetSession.CreateFromX3dhInitiator(
            aliceId,
            aliceDeviceId,
            bobId,
            bobDeviceId,
            aliceResult,
            bobSpkPub,
            _engine);
        aliceSessionResult.IsSuccess.Should().BeTrue();
        using var aliceSession = aliceSessionResult.Value;

        // Bob receives Alice's initial handshake material and derives matching master secret
        var bobX3dh = X3dhAgreement.Receive(
            bobIdentityPriv.Span,
            bobSpkPriv.Span,
            receiverOneTimePreKeyPrivateKeyOrEmpty: default,
            aliceIdentityPub,
            aliceResult.EphemeralPublicKey,
            _engine);
        bobX3dh.IsSuccess.Should().BeTrue();
        var bobMasterSecret = bobX3dh.Value;

        // Bob establishes his inbound Double Ratchet session
        var bobSessionResult = DirectRatchetSession.CreateFromX3dhResponder(
            bobId,
            bobDeviceId,
            aliceId,
            aliceDeviceId,
            bobMasterSecret,
            aliceResult.EphemeralPublicKey,
            _engine);
        bobSessionResult.IsSuccess.Should().BeTrue();
        using var bobSession = bobSessionResult.Value;

        // -------------------------------------------------------------------------
        // 3. BURST MESSAGING WITH REAL AES-256-GCM AEAD ENCRYPTION
        // -------------------------------------------------------------------------
        const int burstCount = 5;
        var sentPackets = new List<(uint Counter, byte[] Ciphertext, byte[] Nonce, DhPublicKey? EphemKey, string Plaintext)>();

        for (int i = 0; i < burstCount; i++)
        {
            var plaintext = $"Real-world confidential message {i} from Alice to Bob";
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

            var stepResult = aliceSession.StepSendingChain(_engine);
            stepResult.IsSuccess.Should().BeTrue();

            var (msgCounter, msgKey, ephemKey) = stepResult.Value;
            var nonce = new byte[12];
            BitConverter.GetBytes((ulong)msgCounter).CopyTo(nonce, 0);

            var ciphertext = _engine.EncryptAesGcm(
                msgKey.Span,
                nonce,
                plaintextBytes,
                associatedData: Encoding.UTF8.GetBytes($"aad-{msgCounter}"));

            sentPackets.Add((msgCounter, ciphertext, nonce, ephemKey, plaintext));
        }

        aliceSession.SendingCounter.Should().Be(burstCount);

        // -------------------------------------------------------------------------
        // 4. OUT-OF-ORDER RECEIPT & SKIPPED-KEY RETRIEVAL
        // -------------------------------------------------------------------------
        // Packets arrive in shuffled order: 4, 1, 3, 0, 2
        var arrivalSequence = new[] { 4, 1, 3, 0, 2 };

        foreach (var index in arrivalSequence)
        {
            var packet = sentPackets[index];

            // If counter is >= bob's current ReceivingCounter, step receiving chain
            MessageKey decryptionKey;
            if (packet.Counter >= bobSession.ReceivingCounter)
            {
                var stepRecv = bobSession.StepReceivingChain(_engine, packet.Counter);
                stepRecv.IsSuccess.Should().BeTrue();
                decryptionKey = stepRecv.Value.Key;
            }
            else
            {
                // Retrieve previously skipped and cached message key
                var skipped = bobSession.TryGetSkippedKey(packet.Counter);
                skipped.IsSuccess.Should().BeTrue();
                decryptionKey = skipped.Value;
            }

            using (decryptionKey)
            {
                var decryptedBytes = _engine.DecryptAesGcm(
                    decryptionKey.Span,
                    packet.Nonce,
                    packet.Ciphertext,
                    associatedData: Encoding.UTF8.GetBytes($"aad-{packet.Counter}"));

                var decryptedText = Encoding.UTF8.GetString(decryptedBytes);
                decryptedText.Should().Be(packet.Plaintext);
            }
        }

        bobSession.ReceivingCounter.Should().Be(burstCount);

        // -------------------------------------------------------------------------
        // 5. ANTI-REPLAY DEFENSE
        // -------------------------------------------------------------------------
        // Re-attempting to retrieve already consumed skipped key must fail
        var replayAttempt = bobSession.TryGetSkippedKey(1);
        replayAttempt.IsFailure.Should().BeTrue();
        replayAttempt.Error.Code.Should().Be("SKIPPED_KEY_NOT_FOUND");

        // Attempting to step receiving chain behind current counter must fail
        var behindCounterAttempt = bobSession.StepReceivingChain(_engine, targetCounter: 2);
        behindCounterAttempt.IsFailure.Should().BeTrue();
        behindCounterAttempt.Error.Code.Should().Be("COUNTER_ALREADY_PASSED");

        // -------------------------------------------------------------------------
        // 6. CONTINUOUS BIDIRECTIONAL MULTI-TURN RATCHETING (20 Alternating Turns)
        // -------------------------------------------------------------------------
        for (int turn = 0; turn < 20; turn++)
        {
            bool isBobSending = (turn % 2 == 0);

            var senderSession = isBobSending ? bobSession : aliceSession;
            var receiverSession = isBobSending ? aliceSession : bobSession;
            var senderName = isBobSending ? "Bob" : "Alice";

            // Sender steps sending chain (which automatically performs DH ratchet if needed)
            var sendStep = senderSession.StepSendingChain(_engine);
            sendStep.IsSuccess.Should().BeTrue();
            var (sendCounter, sendKey, currentSenderEphemKey) = sendStep.Value;

            var turnMessage = $"Turn {turn} payload from {senderName}";
            var turnBytes = Encoding.UTF8.GetBytes(turnMessage);
            var turnNonce = new byte[12];
            BitConverter.GetBytes((ulong)turn).CopyTo(turnNonce, 0);

            var turnCiphertext = _engine.EncryptAesGcm(
                sendKey.Span,
                turnNonce,
                turnBytes,
                associatedData: Encoding.UTF8.GetBytes($"turn-{turn}"));

            // Receiver inspects message's EphemeralPublicKey and ratchets if remote key changed
            if (currentSenderEphemKey != null && currentSenderEphemKey != receiverSession.RemoteEphemeralPublicKey)
            {
                var ratchetResult = receiverSession.StepDhRatchet(currentSenderEphemKey, _engine);
                ratchetResult.IsSuccess.Should().BeTrue();
            }

            // Receiver steps receiving chain to get decryption key
            var recvStep = receiverSession.StepReceivingChain(_engine, sendCounter);
            recvStep.IsSuccess.Should().BeTrue();
            using var recvKey = recvStep.Value.Key;

            var decryptedTurnBytes = _engine.DecryptAesGcm(
                recvKey.Span,
                turnNonce,
                turnCiphertext,
                associatedData: Encoding.UTF8.GetBytes($"turn-{turn}"));

            Encoding.UTF8.GetString(decryptedTurnBytes).Should().Be(turnMessage);
        }

        // -------------------------------------------------------------------------
        // 7. TEARDOWN AND ZEROIZATION
        // -------------------------------------------------------------------------
        aliceSession.Zeroize();
        aliceSession.IsZeroized.Should().BeTrue();

        var failStep = aliceSession.StepSendingChain(_engine);
        failStep.IsFailure.Should().BeTrue();
        failStep.Error.Code.Should().Be("INVALID_SESSION_STATE");
    }
}
