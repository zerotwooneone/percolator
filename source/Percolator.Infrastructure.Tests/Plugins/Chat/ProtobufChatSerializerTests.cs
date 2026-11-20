using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

[TestFixture]
public sealed class ProtobufChatSerializerTests
{
    private readonly ProtobufPayloadSerializer _serializer = new();

    [Test]
    public void PaddedEnvelope_PreservesSteppedBucketSizeAndSupports16KCharMessages()
    {
        // 1. Test small message (bucket 256 or 512)
        var smallMsg = new ChatMessageDto(
            PayloadId.New(),
            ChannelId.New(),
            Domain.Identities.PublicIdentityId.New(),
            1700000000000UL,
            "Hello secure world",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var smallEnvelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Message, smallMsg);
        smallEnvelopeBytes.Length.Should().BeGreaterThan(0);

        var smallEnvResult = _serializer.Deserialize<ChatPayloadEnvelopeDto>(smallEnvelopeBytes);
        smallEnvResult.IsSuccess.Should().BeTrue();
        smallEnvResult.Value!.Type.Should().Be(ChatPayloadType.Message);

        var innerMsgResult = _serializer.Deserialize<ChatMessageDto>(smallEnvResult.Value.Data);
        innerMsgResult.IsSuccess.Should().BeTrue();
        innerMsgResult.Value!.Content.Should().Be("Hello secure world");
        innerMsgResult.Value.Id.Should().Be(smallMsg.Id);

        // Verify padding preserved
        int expectedPadding = SteppedPadding.CalculatePaddedLength(smallEnvResult.Value.Data.Length) - smallEnvResult.Value.Data.Length;
        smallEnvResult.Value.Padding.Length.Should().Be(expectedPadding);

        // 2. Test large message (up to MaxContentLength = 16,000 characters)
        string largeContent = new string('A', 16000);
        var largeMsg = new ChatMessageDto(
            PayloadId.New(),
            ChannelId.New(),
            Domain.Identities.PublicIdentityId.New(),
            1700000000000UL,
            largeContent,
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            3600,
            null);

        var largeEnvelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Message, largeMsg);
        largeEnvelopeBytes.Length.Should().BeGreaterThan(16000);

        var largeEnvResult = _serializer.Deserialize<ChatPayloadEnvelopeDto>(largeEnvelopeBytes);
        largeEnvResult.IsSuccess.Should().BeTrue();
        largeEnvResult.Value!.Type.Should().Be(ChatPayloadType.Message);

        var largeInnerResult = _serializer.Deserialize<ChatMessageDto>(largeEnvResult.Value.Data);
        largeInnerResult.IsSuccess.Should().BeTrue();
        largeInnerResult.Value!.Content.Length.Should().Be(16000);
        largeInnerResult.Value.ExpireAfterSeconds.Should().Be(3600);
    }

    [Test]
    public void ReplyContext_ExcludesQuoteSnippetFromWire()
    {
        var parentId = PayloadId.New();
        var authorId = Domain.Identities.PublicIdentityId.New();
        var replyContext = new ReplyContextDto(parentId, authorId);

        var msg = new ChatMessageDto(
            PayloadId.New(),
            ChannelId.New(),
            Domain.Identities.PublicIdentityId.New(),
            1700000000000UL,
            "Replying without quoted snippet text on wire",
            Array.Empty<MentionDto>(),
            replyContext,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var serialized = _serializer.Serialize(msg);
        var deserialized = _serializer.Deserialize<ChatMessageDto>(serialized);

        deserialized.IsSuccess.Should().BeTrue();
        deserialized.Value!.ReplyContext.Should().NotBeNull();
        deserialized.Value.ReplyContext!.ParentPayloadId.Should().Be(parentId);
        deserialized.Value.ReplyContext.ParentAuthorId.Should().Be(authorId);
    }

    [Test]
    public void DeliveryReceiptDto_RoundTripsThroughProtobufSerialization()
    {
        var channelId = ChannelId.New();
        var delivererId = Domain.Identities.PublicIdentityId.New();
        var upToPayloadId = PayloadId.New();
        ulong deliveredAtMs = 1700000000000UL;

        var dto = new DeliveryReceiptDto(channelId, delivererId, upToPayloadId, deliveredAtMs);

        var serialized = _serializer.Serialize(dto);
        var deserialized = _serializer.Deserialize<DeliveryReceiptDto>(serialized);

        deserialized.IsSuccess.Should().BeTrue();
        deserialized.Value.Should().NotBeNull();
        deserialized.Value!.ChannelId.Should().Be(channelId);
        deserialized.Value.DelivererId.Should().Be(delivererId);
        deserialized.Value.UpToPayloadId.Should().Be(upToPayloadId);
        deserialized.Value.DeliveredAtUnixMs.Should().Be(deliveredAtMs);
    }
}
