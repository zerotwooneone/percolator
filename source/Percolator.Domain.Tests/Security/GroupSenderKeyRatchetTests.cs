using System.Security.Cryptography;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class GroupSenderKeyRatchetTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private ConversationId _conversationId;
    private PublicIdentityId _authorId;
    private DeviceId _authorDeviceId;
    private ChainKey _initialChainKey;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _conversationId = ConversationId.New();
        _authorId = PublicIdentityId.New();
        _authorDeviceId = DeviceId.Primary;

        byte[] rawKey = new byte[32];
        RandomNumberGenerator.Fill(rawKey);
        _initialChainKey = ChainKey.FromSpan(rawKey);
    }

    [Test]
    public void Advance_IncrementsIteration_AndDerivesNewMessageKey()
    {
        var ratchet = new GroupSenderKeyRatchet(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        var result1 = ratchet.Advance(_cryptoEngine);
        var result2 = ratchet.Advance(_cryptoEngine);

        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.Value.Iteration.Should().Be(0);
        result2.Value.Iteration.Should().Be(1);
        result1.Value.Key.Should().NotBe(result2.Value.Key);
        ratchet.Iteration.Should().Be(2);
    }

    [Test]
    public void Dispose_ZeroizesChainKey()
    {
        var ratchet = new GroupSenderKeyRatchet(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        ratchet.Dispose();

        ratchet.IsZeroized.Should().BeTrue();
    }
}
