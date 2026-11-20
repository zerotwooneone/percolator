using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class MailboxQueueIdTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Create_NullOrWhitespace_FailsWithInvalidError(string? invalidValue)
    {
        var result = MailboxQueueId.Create(invalidValue);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_MAILBOX_QUEUE_ID");
    }

    [Test]
    public void Create_ExceedingMaxLength_FailsWithInvalidError()
    {
        var boundaryValue = new string('a', MailboxQueueId.MaxLength);
        var exceedingValue = new string('a', MailboxQueueId.MaxLength + 1);

        var validResult = MailboxQueueId.Create(boundaryValue);
        var invalidResult = MailboxQueueId.Create(exceedingValue);

        validResult.IsSuccess.Should().BeTrue();
        invalidResult.IsFailure.Should().BeTrue();
        invalidResult.Error.Code.Should().Be("INVALID_MAILBOX_QUEUE_ID");
    }

    [Test]
    public void Create_ValidValue_SucceedsAndToStringReturnsValue()
    {
        const string rawId = "queue-ack-12345";

        var result = MailboxQueueId.Create(rawId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be(rawId);
        result.Value.ToString().Should().Be(rawId);
    }

    [Test]
    public void Parse_InvalidValue_ThrowsArgumentException()
    {
        var act = () => MailboxQueueId.Parse("   ");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("value");
    }
}
