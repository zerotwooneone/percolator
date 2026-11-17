// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Tests.Common.Services;

[TestFixture]
public sealed class CurrentPersonaServiceTests
{
    private CurrentPersonaService CreateService()
    {
        return new CurrentPersonaService(NullLogger<CurrentPersonaService>.Instance);
    }

    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var act = () => new CurrentPersonaService(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public void InitializeActivePersonas_SeedsActiveListAndSelectsPrimary()
    {
        using var sut = CreateService();
        sut.CurrentPersona.Should().BeNull();
        sut.ActivePersonas.Should().BeEmpty();

        ActivePersonaModel? emitted = null;
        using var sub = sut.CurrentPersonaChanged.Subscribe(p => emitted = p);

        var id1 = new PublicIdentityId(Guid.NewGuid());
        var id2 = new PublicIdentityId(Guid.NewGuid());
        var persona1 = new ActivePersonaModel(id1, "Alice", 5100);
        var persona2 = new ActivePersonaModel(id2, "Bob", 5102);

        sut.InitializeActivePersonas(new[] { persona1, persona2 });

        sut.ActivePersonas.Should().HaveCount(2);
        sut.CurrentPersona.Should().Be(persona1, "first launched persona must be selected by default");
        emitted.Should().Be(persona1);
    }

    [Test]
    public void SelectPersonaById_WhenFoundInActiveSession_SwitchesPersonaAndReturnsTrue()
    {
        using var sut = CreateService();
        var id1 = new PublicIdentityId(Guid.NewGuid());
        var id2 = new PublicIdentityId(Guid.NewGuid());
        var persona1 = new ActivePersonaModel(id1, "Alice", 5100);
        var persona2 = new ActivePersonaModel(id2, "Bob", 5102);

        sut.InitializeActivePersonas(new[] { persona1, persona2 });

        ActivePersonaModel? emitted = null;
        using var sub = sut.CurrentPersonaChanged.Subscribe(p => emitted = p);

        var result = sut.SelectPersonaById(id2);

        result.Should().BeTrue();
        sut.CurrentPersona.Should().Be(persona2);
        emitted.Should().Be(persona2);
    }

    [Test]
    public void SelectPersonaById_WhenNotFoundInActiveSession_ReturnsFalseAndLeavesCurrentPersonaUnchanged()
    {
        using var sut = CreateService();
        var id1 = new PublicIdentityId(Guid.NewGuid());
        var persona1 = new ActivePersonaModel(id1, "Alice", 5100);

        sut.InitializeActivePersonas(new[] { persona1 });

        var missingId = new PublicIdentityId(Guid.NewGuid());
        var result = sut.SelectPersonaById(missingId);

        result.Should().BeFalse();
        sut.CurrentPersona.Should().Be(persona1);
    }

    [Test]
    public void SelectPersona_UpdatesCurrentPersona_AndAddsToActiveListIfNotPresent()
    {
        using var sut = CreateService();
        var id = new PublicIdentityId(Guid.NewGuid());
        var persona = new ActivePersonaModel(id, "Charlie", 5104);

        sut.SelectPersona(persona);

        sut.CurrentPersona.Should().Be(persona);
        sut.ActivePersonas.Should().Contain(persona);
    }

    [Test]
    public void Dispose_DisposesReactiveStreamCleanly()
    {
        var sut = CreateService();
        sut.Dispose();

        var act = () => sut.SelectPersona(new ActivePersonaModel(PublicIdentityId.New(), "Dave", 5100));
        act.Should().NotThrow();
    }
}
