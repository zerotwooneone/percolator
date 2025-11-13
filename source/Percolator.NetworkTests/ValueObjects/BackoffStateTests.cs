using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.ValueObjects;

[TestFixture]
public class BackoffStateTests
{
    [Test]
    public void Initially_CanAttempt_IsTrue_WhenNoBackoff()
    {
        var state = new BackoffState();
        var budget = new RetryBudget(3, TimeSpan.FromMinutes(1));
        state.CanAttempt(DateTimeOffset.UtcNow, budget).Should().BeTrue();
    }

    [Test]
    public void RegisterFailure_IncrementsAttempts_SetsTimes_And_GatesAttempts()
    {
        var state = new BackoffState();
        var budget = new RetryBudget(2, TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;

        state.RegisterFailure(now, TimeSpan.FromSeconds(5));
        state.Attempts.Should().Be(1);
        state.LastFailureAt.Should().Be(now);
        state.NextEligibleAt.Should().BeOnOrAfter(now.AddSeconds(5));
        state.CanAttempt(now.AddSeconds(1), budget).Should().BeFalse("backoff gating");
        state.CanAttempt(now.AddSeconds(6), budget).Should().BeTrue();

        state.RegisterFailure(now.AddSeconds(6), TimeSpan.FromSeconds(5));
        state.Attempts.Should().Be(2);
        state.CanAttempt(now.AddSeconds(7), budget).Should().BeFalse("exhausted budget");
    }
}
