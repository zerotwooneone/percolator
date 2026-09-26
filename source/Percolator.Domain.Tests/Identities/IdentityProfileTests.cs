using Percolator.Domain.Common;
using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public class IdentityProfileTests
{
    private FakeDateTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void Create_WithValidParameters_ReturnsActiveProfile_AndEmitsCreatedEvent()
    {
        var id = PublicIdentityId.New();
        var result = IdentityProfile.Create(id, "Work Persona", IdentityRole.UserPersona, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        var profile = result.Value;
        profile.Id.Should().Be(id);
        profile.DisplayName.Should().Be("Work Persona");
        profile.Role.Should().Be(IdentityRole.UserPersona);
        profile.State.Should().Be(IdentityState.Active);
        profile.CreatedAtUtc.Should().Be(_timeProvider.UtcNow);
        profile.DisabledAtUtc.Should().BeNull();

        profile.DomainEvents.Should().ContainSingle(e => e is IdentityCreatedEvent);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Create_WithInvalidDisplayName_ReturnsValidationError(string? invalidName)
    {
        var result = IdentityProfile.Create(PublicIdentityId.New(), invalidName!, IdentityRole.UserPersona, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_DISPLAY_NAME");
    }

    [Test]
    public void Disable_WhenActive_TransitionsToDisabled_AndEmitsIdentityDisabledEvent()
    {
        var profile = IdentityProfile.Create(PublicIdentityId.New(), "Work", IdentityRole.UserPersona, _timeProvider).Value;
        profile.ClearDomainEvents();

        _timeProvider.Advance(TimeSpan.FromHours(8));
        var result = profile.Disable(_timeProvider);

        result.IsSuccess.Should().BeTrue();
        profile.State.Should().Be(IdentityState.Disabled);
        profile.DisabledAtUtc.Should().Be(_timeProvider.UtcNow);

        profile.DomainEvents.Should().ContainSingle(e => e is IdentityDisabledEvent);
        var disabledEvent = (IdentityDisabledEvent)profile.DomainEvents.Single();
        disabledEvent.IdentityId.Should().Be(profile.Id);
        disabledEvent.OccurredOnUtc.Should().Be(_timeProvider.UtcNow);
    }

    [Test]
    public void Disable_WhenAlreadyDisabled_IsIdempotent_AndEmitsNoDuplicateEvent()
    {
        var profile = IdentityProfile.Create(PublicIdentityId.New(), "Work", IdentityRole.UserPersona, _timeProvider).Value;
        profile.Disable(_timeProvider);
        profile.ClearDomainEvents();

        var result = profile.Disable(_timeProvider);

        result.IsSuccess.Should().BeTrue();
        profile.DomainEvents.Should().BeEmpty();
    }

    [Test]
    public void Enable_WhenDisabled_TransitionsToActive_AndEmitsIdentityEnabledEvent()
    {
        var profile = IdentityProfile.Create(PublicIdentityId.New(), "Work", IdentityRole.UserPersona, _timeProvider).Value;
        profile.Disable(_timeProvider);
        profile.ClearDomainEvents();

        _timeProvider.Advance(TimeSpan.FromHours(14));
        var result = profile.Enable(_timeProvider);

        result.IsSuccess.Should().BeTrue();
        profile.State.Should().Be(IdentityState.Active);
        profile.DisabledAtUtc.Should().BeNull();

        profile.DomainEvents.Should().ContainSingle(e => e is IdentityEnabledEvent);
    }
}
