namespace Percolator.Infrastructure.Tests.Packaging;

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Serialization;

[TestFixture]
public sealed class ProtobufSessionWirePackerTests
{
    private readonly ProtobufSessionWirePacker _packer = new();

    [Test]
    public void RoundtripDirectSession_PacksAndUnpacksCorrectly()
    {
        var dhKey = DhPublicKey.FromSpan(new byte[32]);
        var header = new RatchetHeader(dhKey, 5, 2);
        byte[] ciphertext = [1, 2, 3, 4, 5, 6, 7, 8];

        var packed = _packer.PackDirectRatchetMessage(header, ciphertext);
        packed.Length.Should().BeGreaterThan(0);

        var channelId = new ChannelId(Guid.NewGuid());
        var recipientId = new PublicIdentityId(Guid.NewGuid());
        var senderId = new PublicIdentityId(Guid.NewGuid());
        var senderDeviceId = new DeviceId(1);
        var now = DateTimeOffset.UtcNow;

        var unpackResult = _packer.UnpackDirectRatchetMessage(
            channelId,
            recipientId,
            senderId,
            senderDeviceId,
            packed,
            now);

        unpackResult.IsSuccess.Should().BeTrue();
        var envelope = unpackResult.Value!;
        envelope.ChannelId.Should().Be(channelId);
        envelope.RecipientIdentityId.Should().Be(recipientId);
        envelope.SenderIdentityId.Should().Be(senderId);
        envelope.SenderDeviceId.Should().Be(senderDeviceId);
        envelope.Header.Counter.Should().Be(5);
        envelope.Header.PreviousChainLength.Should().Be(2);
        envelope.Ciphertext.ToArray().Should().Equal(ciphertext);
        envelope.ReceivedAtUtc.Should().Be(now);
    }

    [Test]
    public void GroupSessionPackage_PacksIterationAndSignature()
    {
        var channelId = new ChannelId(Guid.NewGuid());
        uint iteration = 42;
        byte[] signature = new byte[64];
        signature[0] = 0xAA;
        byte[] ciphertext = [10, 20, 30, 40];

        var packed = _packer.PackGroupMessage(channelId, iteration, signature, ciphertext);
        packed.Length.Should().BeGreaterThan(0);

        var recipientId = new PublicIdentityId(Guid.NewGuid());
        var authorId = new PublicIdentityId(Guid.NewGuid());
        var authorDeviceId = new DeviceId(2);
        var now = DateTimeOffset.UtcNow;

        var unpackResult = _packer.UnpackGroupMessage(
            channelId,
            recipientId,
            authorId,
            authorDeviceId,
            packed,
            now);

        unpackResult.IsSuccess.Should().BeTrue();
        var envelope = unpackResult.Value!;
        envelope.ChannelId.Should().Be(channelId);
        envelope.RecipientIdentityId.Should().Be(recipientId);
        envelope.AuthorIdentityId.Should().Be(authorId);
        envelope.AuthorDeviceId.Should().Be(authorDeviceId);
        envelope.Iteration.Should().Be(iteration);
        envelope.Signature.ToArray().Should().Equal(signature);
        envelope.Ciphertext.ToArray().Should().Equal(ciphertext);
    }
}
