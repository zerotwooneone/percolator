using FluentAssertions;
using Google.Protobuf;
using Moq;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class InitiatorFinalizerTests
{
    [Test]
    public void Finalize_FromInitialRootKey_ParsesSessionId_FromResponderInnerHello()
    {
        // ARRANGE
        var initialRoot = SharedSecret.FromBytes(new byte[32]);
        var responderMessage = SessionRatchetMessage.FromBytes(new byte[] { 0x01, 0x02 });

        // Fake inner payload with direct_session_id
        var inner = new ResponderInnerHello
        {
            Version = 1,
            DirectSessionId = "session-123"
        };
        byte[] innerBytes;
        using (var ms = new System.IO.MemoryStream())
        {
            inner.WriteTo(ms);
            innerBytes = ms.ToArray();
        }

        var ratchet = new Mock<IRatchetEngine>(MockBehavior.Strict);
        ratchet
            .Setup(r => r.Decrypt(
                It.IsAny<RatchetState>(),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<AssociatedData>()))
            .Returns((Plaintext.FromBytes(innerBytes), new RatchetState(RootKey.FromBytes(initialRoot.ToArray()), null, 0, null, 0, 0, null, null, 1)));

        var finalizer = new InitiatorFinalizer(ratchet.Object);

        // ACT
        var (sessionId, newState) = finalizer.Finalize(initialRoot, responderMessage);

        // ASSERT
        sessionId.Should().Be("session-123");
        newState.Should().NotBeNull();
    }
}
