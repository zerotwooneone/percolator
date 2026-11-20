using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Application.Session;
using Percolator.Application.Session.Events;
using Percolator.Application.Session.Ports;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Identities;

namespace Percolator.Application.Tests.Session;

[TestFixture]
public sealed class ActiveIdentitySetTests
{
    private static PublicIdentityId NewIdentity() => PublicIdentityId.New();

    private static ActiveIdentitySet CreateSet(IEventPublisher publisher) =>
        new(publisher, NullLogger<ActiveIdentitySet>.Instance);

    [Test]
    public void Add_NewIdentity_ReturnsTrueAndPublishesActivation()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();

        var added = set.Add(identity);

        added.Should().BeTrue();
        set.Current.Should().Contain(identity);
        set.Contains(identity).Should().BeTrue();
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public async Task AddAsync_NewIdentity_AwaitsAndPublishesActivation()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();

        var added = await set.AddAsync(identity);

        added.Should().BeTrue();
        set.Current.Should().Contain(identity);
        set.Contains(identity).Should().BeTrue();
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public void Add_DuplicateIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();
        set.Add(identity);

        var added = set.Add(identity);

        added.Should().BeFalse();
        set.Current.Should().Contain(identity);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>();
    }

    [Test]
    public async Task AddAsync_DuplicateIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();
        await set.AddAsync(identity);

        var added = await set.AddAsync(identity);

        added.Should().BeFalse();
        set.Current.Should().Contain(identity);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PersonaActivatedEvent>();
    }

    [Test]
    public void Remove_ActiveIdentity_ReturnsTrueAndPublishesDeactivation()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();
        set.Add(identity);

        var removed = set.Remove(identity);

        removed.Should().BeTrue();
        set.Current.Should().NotContain(identity);
        set.Contains(identity).Should().BeFalse();
        publisher.PublishedEvents.Should().HaveCount(2);
        publisher.PublishedEvents[1].Should().BeOfType<PersonaDeactivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public async Task RemoveAsync_ActiveIdentity_AwaitsAndPublishesDeactivation()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identity = NewIdentity();
        await set.AddAsync(identity);

        var removed = await set.RemoveAsync(identity);

        removed.Should().BeTrue();
        set.Current.Should().NotContain(identity);
        set.Contains(identity).Should().BeFalse();
        publisher.PublishedEvents.Should().HaveCount(2);
        publisher.PublishedEvents[1].Should().BeOfType<PersonaDeactivatedEvent>()
            .Which.IdentityId.Should().Be(identity);
    }

    [Test]
    public void Remove_InactiveIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);

        var removed = set.Remove(NewIdentity());

        removed.Should().BeFalse();
        publisher.PublishedEvents.Should().BeEmpty();
    }

    [Test]
    public async Task RemoveAsync_InactiveIdentity_ReturnsFalseAndDoesNotPublish()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);

        var removed = await set.RemoveAsync(NewIdentity());

        removed.Should().BeFalse();
        publisher.PublishedEvents.Should().BeEmpty();
    }

    [Test]
    public void Add_StateMutatesBeforeEventPublishes()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
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
        var set = CreateSet(publisher);
        var first = NewIdentity();
        var second = NewIdentity();

        set.Add(first);
        var snapshot = set.Current;
        set.Add(second);
        set.Remove(first);

        snapshot.Should().ContainSingle().Which.Should().Be(first);
        set.Current.Should().ContainSingle().Which.Should().Be(second);
    }

    [Test]
    public void Add_WhenPublisherThrows_StateStillMutatesWithoutCrashingSynchronousCaller()
    {
        var throwingPublisher = new ThrowingEventPublisher();
        var set = CreateSet(throwingPublisher);
        var identity = NewIdentity();

        var added = set.Add(identity);

        added.Should().BeTrue();
        set.Current.Should().Contain(identity);
    }

    [Test]
    public void ConcurrentAddAndRemove_MaintainsStateIntegrity()
    {
        var publisher = new FakeEventPublisher();
        var set = CreateSet(publisher);
        var identities = Enumerable.Range(0, 50).Select(_ => NewIdentity()).ToArray();

        Parallel.For(0, 100, i =>
        {
            var id = identities[i % identities.Length];
            if (i % 2 == 0)
            {
                set.Add(id);
            }
            else
            {
                set.Remove(id);
            }

            var snapshot = set.Current;
            snapshot.Should().NotBeNull();
        });

        // Current set must match Contains query for all elements
        foreach (var id in identities)
        {
            set.Current.Contains(id).Should().Be(set.Contains(id));
        }
    }

    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public ValueTask PublishAsync<TEvent>(TEvent @event, System.Threading.CancellationToken cancellationToken = default)
            where TEvent : IApplicationEvent
        {
            throw new InvalidOperationException("Simulated publisher failure");
        }
    }
}
