using FluentAssertions;
using Percolator.Domain.Security.Model;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests.Builders;

/// <summary>
/// Automation helper to run multi-turn conversational scripts between Double Ratchet sessions,
/// verifying bidirectional step ratcheting and AEAD payload delivery.
/// </summary>
public static class ConversationScriptRunner
{
    public static void ExchangeAlternatingTurns(
        DirectRatchetSession sessionA,
        string nameA,
        DirectRatchetSession sessionB,
        string nameB,
        int turns,
        ScenarioCryptoEngine engine)
    {
        for (int i = 0; i < turns; i++)
        {
            var (sender, receiver, senderName) = (i % 2 == 0)
                ? (sessionA, sessionB, nameA)
                : (sessionB, sessionA, nameB);

            var messageText = $"Turn {i} from {senderName}";
            var packet = WirePacketSimulator.PackDirect(sender, messageText, engine);
            var decrypted = WirePacketSimulator.UnpackDirect(receiver, packet, engine);

            decrypted.Should().Be(messageText);
        }
    }
}
