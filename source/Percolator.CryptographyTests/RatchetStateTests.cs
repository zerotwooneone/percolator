using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class RatchetStateTests
{
    [Test]
    public void Construct_WithValidInputs_SetsProperties()
    {
        var root = new RootKey(RandomNumberGenerator.GetBytes(32));
        var send = new ChainKey(RandomNumberGenerator.GetBytes(32));
        var recv = new ChainKey(RandomNumberGenerator.GetBytes(32));
        var remote = new RatchetEphemeralKey(RandomNumberGenerator.GetBytes(91)); // typical SPKI length
        var priv = new PrivateEphemeralKey(RandomNumberGenerator.GetBytes(32));

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
    public void Snapshot_Roundtrip_PreservesValues()
    {
        var root = new RootKey(RandomNumberGenerator.GetBytes(32));
        var send = new ChainKey(RandomNumberGenerator.GetBytes(32));
        var recv = new ChainKey(RandomNumberGenerator.GetBytes(32));
        var remote = new RatchetEphemeralKey(RandomNumberGenerator.GetBytes(91));
        var priv = new PrivateEphemeralKey(RandomNumberGenerator.GetBytes(32));

        var original = new RatchetState(
            root, send, 5, recv, 7, 3, remote, priv, skippedKeyLimit: 2000);

        var bytes = original.ToSnapshotBytes();
        var restored = RatchetState.FromSnapshotBytes(bytes);

        restored.Should().NotBeSameAs(original);
        restored.RootKey.Should().BeEquivalentTo(original.RootKey);
        restored.SendingChainKey.Should().BeEquivalentTo(original.SendingChainKey);
        restored.ReceivingChainKey.Should().BeEquivalentTo(original.ReceivingChainKey);
        restored.SendingCounter.Should().Be(5);
        restored.ReceivingCounter.Should().Be(7);
        restored.PreviousChainLength.Should().Be(3);
        restored.RemoteRatchetKey.Should().BeEquivalentTo(original.RemoteRatchetKey);
        restored.DhRatchetPrivateKey.Should().BeEquivalentTo(original.DhRatchetPrivateKey);
        restored.SkippedKeyLimit.Should().Be(2000);
    }

    [Test]
    public void Construct_WithInvalidSkippedKeyLimit_Throws()
    {
        var root = new RootKey(RandomNumberGenerator.GetBytes(32));
        Action act = () => _ = new RatchetState(root, null, 0, null, 0, 0, null, null, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
