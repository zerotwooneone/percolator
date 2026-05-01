using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class RatchetStateTests
{
    [Test]
    public void Construct_WithValidInputs_SetsProperties()
    {
        var root = RootKey.FromBytes(RandomNumberGenerator.GetBytes(32));
        var send = ChainKey.FromBytes(RandomNumberGenerator.GetBytes(32));
        var recv = ChainKey.FromBytes(RandomNumberGenerator.GetBytes(32));
        var remote = RatchetEphemeralKey.FromBytes(RandomNumberGenerator.GetBytes(91)); // typical SPKI length
        var priv = PrivateEphemeralKey.FromBytes(RandomNumberGenerator.GetBytes(100));

        var state = new RatchetState(
            root,
            send,
            1,
            recv,
            2,
            0,
            remote,
            priv,
            skippedKeyLimit: 1000);

        state.RootKey.Should().Be(root);
        state.SendingChainKey.Should().Be(send);
        state.ReceivingChainKey.Should().Be(recv);
        state.SendingCounter.Should().Be(1);
        state.ReceivingCounter.Should().Be(2);
        state.PreviousChainLength.Should().Be(0);
        state.RemoteRatchetKey.Should().Be(remote);
        state.DhRatchetPrivateKey.Should().Be(priv);
        state.SkippedKeyLimit.Should().Be(1000);
    }

    [Test]
    public void Construct_WithNullRoot_Throws()
    {
        Action act = () => _ = new RatchetState(
            null!,
            null,
            0,
            null,
            0,
            0,
            null,
            null,
            skippedKeyLimit: 100);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Construct_WithInvalidSkippedKeyLimit_Throws()
    {
        var root = RootKey.FromBytes(RandomNumberGenerator.GetBytes(32));
        Action act = () => _ = new RatchetState(root, null, 0, null, 0, 0, null, null, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
