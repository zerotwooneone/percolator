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
    private ChainKey _initialChainKey = null!;

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

    [TearDown]
    public void TearDown()
    {
        _initialChainKey?.Dispose();
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

        var result = ratchet.Advance(_cryptoEngine);

        result.IsSuccess.Should().BeTrue();
        result.Value.Iteration.Should().Be(0);
        result.Value.Key.Should().NotBeNull();
        ratchet.Iteration.Should().Be(1);
    }

    [Test]
    public void Advance_WhenZeroized_ReturnsError()
    {
        var ratchet = new GroupSenderKeyRatchet(
            _conversationId,
            _authorId,
            _authorDeviceId,
            _initialChainKey,
            initialIteration: 0);

        ratchet.Dispose();

        var result = ratchet.Advance(_cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_RATCHET_STATE");
    }
}
