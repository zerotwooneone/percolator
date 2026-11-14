using Percolator.Application.Session;
using Percolator.Application.Session.Events;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Identities;

namespace Percolator.Application.Tests.Session;

[TestFixture]
public sealed class ActiveIdentitySetTests
{
    private static PublicIdentityId NewIdentity() => PublicIdentityId.New();

    [Test]
    public void Add_NewIdentity_ReturnsTrueAndPublishesActivation()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);
        var identity = NewIdentity();

        var added = set.Add(identity);

        added.Should().BeTrue();
        set.Current.Should().Contain(identity);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public void Add_DuplicateIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);
        var identity = NewIdentity();
        set.Add(identity);

        var added = set.Add(identity);

        added.Should().BeFalse();
        set.Current.Should().Contain(identity);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>();
    }

    [Test]
    public void Remove_ActiveIdentity_ReturnsTrueAndPublishesDeactivation()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);
        var identity = NewIdentity();
        set.Add(identity);

        var removed = set.Remove(identity);

        removed.Should().BeTrue();
        set.Current.Should().NotContain(identity);
        publisher.PublishedEvents.Should().HaveCount(2);
        publisher.PublishedEvents[1].Should().BeOfType<PersonaDeactivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public void Remove_InactiveIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);

        var removed = set.Remove(NewIdentity());

        removed.Should().BeFalse();
        publisher.PublishedEvents.Should().BeEmpty();
    }

    [Test]
    public void Add_StateMutatesBeforeEventPublishes()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);
        var identity = NewIdentity();

        set.Add(identity);

        // The event is only observable after the mutation landed, so any
        // subscriber reconciling on receipt must already see the identity.
        set.Current.Should().Contain(identity);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>();
    }

    [Test]
    public void Current_ReturnsStableSnapshot_IsolatedFromLaterMutations()
    {
        var publisher = new FakeEventPublisher();
        var set = new ActiveIdentitySet(publisher);
        var first = NewIdentity();
        var second = NewIdentity();

        set.Add(first);
        var snapshot = set.Current;
        set.Add(second);
        set.Remove(first);

        snapshot.Should().ContainSingle().Which.Should().Be(first);
        set.Current.Should().ContainSingle().Which.Should().Be(second);
    }
}
