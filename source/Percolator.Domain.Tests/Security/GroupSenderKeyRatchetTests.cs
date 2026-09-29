using System.Security.Cryptography;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public class GroupSenderKeyRatchetTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private ChannelId _channelId;
    private PublicIdentityId _authorId;
    private DeviceId _authorDeviceId;
    private ChainKey _initialChainKey = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _channelId = ChannelId.New();
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
        using var ratchet = new GroupSenderKeyRatchet(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        var result = ratchet.Advance(_cryptoEngine);

        result.IsSuccess.Should().BeTrue();
        result.Value.Iteration.Should().Be(0);
        ratchet.Iteration.Should().Be(1);

        result.Value.Key.Dispose();
    }

    [Test]
    public void Zeroize_ClearsChainKey_AndPreventsFurtherAdvances()
    {
        var ratchet = new GroupSenderKeyRatchet(
            _channelId,
            _authorId,
            _authorDeviceId,
            _initialChainKey);

        ratchet.Zeroize();

        ratchet.IsZeroized.Should().BeTrue();
        var result = ratchet.Advance(_cryptoEngine);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_RATCHET_STATE");
    }
}
