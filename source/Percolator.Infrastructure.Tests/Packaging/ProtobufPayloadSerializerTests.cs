using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Tests.Packaging;

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

[TestFixture]
public sealed class ProtobufPayloadSerializerTests
{
    private readonly ProtobufPayloadSerializer _serializer = new();

    [Test]
    public void GenericPayloadSerialization_RoundtripsCorrectly()
    {
        var sessionId = TransferSessionId.FromGuid(Guid.NewGuid());
        var reason = TransferTerminationReason.SenderCancelled;
        var cancelPayload = new FileTransferCancelPayload(sessionId, reason);

        var bytes = _serializer.Serialize(cancelPayload);
        bytes.Length.Should().BeGreaterThan(0);

        var result = _serializer.Deserialize<FileTransferCancelPayload>(bytes);
        result.IsSuccess.Should().BeTrue();
        result.Value!.TransferSessionId.Should().Be(sessionId);
        result.Value.Reason.Should().Be(reason);
    }
}
