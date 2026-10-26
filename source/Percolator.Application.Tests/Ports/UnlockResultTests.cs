using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Application.Tests.TestDoubles;

namespace Percolator.Application.Tests.Ports;

[TestFixture]
public sealed class UnlockResultTests
{
    [Test]
    public void Success_HoldsProvidedSession_AndEvaluatesRecordEquality()
    {
        var session = new FakeAuthenticatedSession();
        var result1 = new UnlockResult.Success(session);
        var result2 = new UnlockResult.Success(session);

        result1.Session.Should().BeSameAs(session);
        result1.Should().Be(result2);
        (result1 == result2).Should().BeTrue();
    }

    [Test]
    public void Success_WithDifferentSessions_AreNotEqual()
    {
        var session1 = new FakeAuthenticatedSession();
        var session2 = new FakeAuthenticatedSession();

        var result1 = new UnlockResult.Success(session1);
        var result2 = new UnlockResult.Success(session2);

        result1.Should().NotBe(result2);
        (result1 == result2).Should().BeFalse();
    }

    [Test]
    public void DuressActivated_EvaluatesRecordEquality()
    {
        var result1 = new UnlockResult.DuressActivated();
        var result2 = new UnlockResult.DuressActivated();

        result1.Should().Be(result2);
        (result1 == result2).Should().BeTrue();
    }

    [Test]
    public void InvalidCredentials_EvaluatesRecordEquality()
    {
        var result1 = new UnlockResult.InvalidCredentials();
        var result2 = new UnlockResult.InvalidCredentials();

        result1.Should().Be(result2);
        (result1 == result2).Should().BeTrue();
    }

    [Test]
    public void DistinctResultSubtypes_AreNeverEqual()
    {
        var session = new FakeAuthenticatedSession();
        UnlockResult success = new UnlockResult.Success(session);
        UnlockResult duress = new UnlockResult.DuressActivated();
        UnlockResult invalid = new UnlockResult.InvalidCredentials();

        success.Should().NotBe(duress);
        success.Should().NotBe(invalid);
        duress.Should().NotBe(invalid);
    }

    [Test]
    public void PatternMatching_CanExhaustivelySwitchOverAllSubtypes()
    {
        var session = new FakeAuthenticatedSession();
        var results = new UnlockResult[]
        {
            new UnlockResult.Success(session),
            new UnlockResult.DuressActivated(),
            new UnlockResult.InvalidCredentials()
        };

        var classifications = results.Select(r => r switch
        {
            UnlockResult.Success s => $"Success({s.Session != null})",
            UnlockResult.DuressActivated => "Duress",
            UnlockResult.InvalidCredentials => "Invalid",
            _ => throw new InvalidOperationException("Unreachable")
        }).ToArray();

        classifications.Should().Equal("Success(True)", "Duress", "Invalid");
    }
}
