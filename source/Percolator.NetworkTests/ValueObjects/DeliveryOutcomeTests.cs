using FluentAssertions;
using NUnit.Framework;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.ValueObjects;

[TestFixture]
public class DeliveryOutcomeTests
{
    [Test]
    public void Success_Factory_Sets_IsSuccess_True_And_NoReason()
    {
        var o = DeliveryOutcome.Success();
        o.IsSuccess.Should().BeTrue();
        o.FailureReason.Should().BeNull();
    }

    [Test]
    public void Failed_Factory_Sets_IsSuccess_False_And_Reason()
    {
        var o = DeliveryOutcome.Failed(DeliveryFailureReason.Timeout);
        o.IsSuccess.Should().BeFalse();
        o.FailureReason.Should().Be(DeliveryFailureReason.Timeout);
    }
}
