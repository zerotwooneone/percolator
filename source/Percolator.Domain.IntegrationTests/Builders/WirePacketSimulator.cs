using System.Text;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests.Builders;

public sealed record WirePacket(
    uint Counter,
    byte[] Ciphertext,
    byte[] Nonce,
    DhPublicKey? EphemeralPublicKey,
    byte[]? Signature = null,
    byte[]? AssociatedData = null,
    string? Plaintext = null)
{
    public byte[] Ciphertext { get; set; } = Ciphertext;
    public byte[] Nonce { get; set; } = Nonce;
    public byte[]? Signature { get; set; } = Signature;

    public void CorruptCiphertext()
    {
        if (Ciphertext.Length > 0)
        {
            Ciphertext[0] ^= 0xFF;
        }
    }

    public void CorruptSignature()
    {
        if (Signature != null && Signature.Length > 0)
        {
            Signature[0] ^= 0xFF;
        }
    }

    public void CorruptNonce()
    {
        if (Nonce.Length > 0)
        {
            Nonce[0] ^= 0xFF;
        }
    }
}

public static class WirePacketSimulator
{
    public static WirePacket PackDirect(
        DirectRatchetSession senderSession,
        string plaintext,
        ScenarioCryptoEngine engine,
        byte[]? associatedData = null)
    {
        var step = senderSession.StepSendingChain(engine);
        if (step.IsFailure)
        {
            throw new InvalidOperationException($"Cannot step sending chain: {step.Error.Description}");
        }

        var (counter, messageKey, ephemKey) = step.Value;
        using (messageKey)
        {
            var nonce = new byte[12];
            BitConverter.GetBytes((ulong)counter).CopyTo(nonce, 0);

            var aad = associatedData ?? Encoding.UTF8.GetBytes($"aad-{counter}");
            var ciphertext = engine.EncryptAesGcm(
                messageKey.Span,
                nonce,
                Encoding.UTF8.GetBytes(plaintext),
                aad);

            return new WirePacket(counter, ciphertext, nonce, ephemKey, AssociatedData: aad, Plaintext: plaintext);
        }
    }

    public static string UnpackDirect(
        DirectRatchetSession receiverSession,
        WirePacket packet,
        ScenarioCryptoEngine engine)
    {
        if (packet.EphemeralPublicKey != null && packet.EphemeralPublicKey != receiverSession.RemoteEphemeralPublicKey)
        {
            var ratchetResult = receiverSession.StepDhRatchet(packet.EphemeralPublicKey, engine);
            if (ratchetResult.IsFailure)
            {
                throw new InvalidOperationException($"DH ratchet failed: {ratchetResult.Error.Description}");
            }
        }

        MessageKey decryptionKey;
        if (packet.Counter >= receiverSession.ReceivingCounter)
        {
            var stepRecv = receiverSession.StepReceivingChain(engine, packet.Counter);
            if (stepRecv.IsFailure)
            {
                throw new InvalidOperationException($"Step receiving chain failed: {stepRecv.Error.Description}");
            }
            decryptionKey = stepRecv.Value.Key;
        }
        else
        {
            var skipped = receiverSession.TryGetSkippedKey(packet.Counter);
            if (skipped.IsFailure)
            {
                throw new InvalidOperationException($"Try get skipped key failed: {skipped.Error.Description}");
            }
            decryptionKey = skipped.Value;
        }

        using (decryptionKey)
        {
            var decryptedBytes = engine.DecryptAesGcm(
                decryptionKey.Span,
                packet.Nonce,
                packet.Ciphertext,
                packet.AssociatedData ?? Encoding.UTF8.GetBytes($"aad-{packet.Counter}"));

            return Encoding.UTF8.GetString(decryptedBytes);
        }
    }

    public static WirePacket PackGroup(
        GroupSenderKeyRatchet senderRatchet,
        string plaintext,
        ScenarioCryptoEngine engine,
        byte[]? associatedData = null)
    {
        var advance = senderRatchet.Advance(engine);
        if (advance.IsFailure)
        {
            throw new InvalidOperationException($"Sender ratchet advance failed: {advance.Error.Description}");
        }

        var (iteration, messageKey) = advance.Value;
        using (messageKey)
        {
            var textBytes = Encoding.UTF8.GetBytes(plaintext);
            var sigResult = senderRatchet.SignPayload(textBytes, engine);
            if (sigResult.IsFailure)
            {
                throw new InvalidOperationException($"Signing failed: {sigResult.Error.Description}");
            }

            var nonce = new byte[12];
            BitConverter.GetBytes((ulong)iteration).CopyTo(nonce, 0);

            var aad = associatedData ?? Encoding.UTF8.GetBytes($"group-{senderRatchet.ChannelId}-{iteration}");
            var ciphertext = engine.EncryptAesGcm(
                messageKey.Span,
                nonce,
                textBytes,
                aad);

            return new WirePacket(iteration, ciphertext, nonce, EphemeralPublicKey: null, Signature: sigResult.Value, AssociatedData: aad, Plaintext: plaintext);
        }
    }

    public static string UnpackGroup(
        GroupReceiverSession receiverSession,
        WirePacket packet,
        ScenarioCryptoEngine engine)
    {
        var advance = receiverSession.TryAdvanceToIteration(packet.Counter, engine);
        if (advance.IsFailure)
        {
            throw new InvalidOperationException($"Receiver session advance failed: {advance.Error.Description}");
        }

        using var messageKey = advance.Value;
        var aad = packet.AssociatedData ?? Encoding.UTF8.GetBytes($"group-{receiverSession.ChannelId}-{packet.Counter}");

        var decryptedBytes = engine.DecryptAesGcm(
            messageKey.Span,
            packet.Nonce,
            packet.Ciphertext,
            aad);

        if (packet.Signature != null)
        {
            var sigVerify = receiverSession.VerifyAuthorSignature(decryptedBytes, packet.Signature, engine);
            if (sigVerify.IsFailure)
            {
                throw new InvalidOperationException($"Author signature verification failed: {sigVerify.Error.Description}");
            }
        }

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}
