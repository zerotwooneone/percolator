// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class DesktopPeerConnectionServiceTests
{
    private ServiceCollection _services = null!;
    private ServiceProvider _serviceProvider = null!;
    private Mock<ICurrentPersonaService> _currentPersonaMock = null!;
    private FakeDesktopPersonaDirectory _fakeDirectory = null!;
    private IStreamRegistryManagement _fakeStreamRegistry = null!;
    private FakeDateTimeProvider _fakeTimeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _currentPersonaMock = new Mock<ICurrentPersonaService>();
        _currentPersonaMock.Setup(a => a.CurrentPersona).Returns((ActivePersonaModel?)null);
        _fakeDirectory = new FakeDesktopPersonaDirectory();
        _fakeStreamRegistry = Mock.Of<IStreamRegistryManagement>();
        _fakeTimeProvider = new FakeDateTimeProvider();

        _services = new ServiceCollection();
        _services.AddSingleton<ICurrentPersonaService>(_currentPersonaMock.Object);
        _services.AddSingleton<IPersonaDirectory>(_fakeDirectory);
        _serviceProvider = _services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }

    [Test]
    public void Constructor_WithNullArguments_ThrowsArgumentNullException()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        Action act1 = () => new DesktopPeerConnectionService(null!, _currentPersonaMock.Object, _fakeStreamRegistry, _fakeTimeProvider);
        Action act2 = () => new DesktopPeerConnectionService(scopeFactory, null!, _fakeStreamRegistry, _fakeTimeProvider);
        Action act3 = () => new DesktopPeerConnectionService(scopeFactory, _currentPersonaMock.Object, null!, _fakeTimeProvider);
        Action act4 = () => new DesktopPeerConnectionService(scopeFactory, _currentPersonaMock.Object, _fakeStreamRegistry, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectToPeerAsync_WhenNoActivePersonaExists_ReturnsFailure()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var sut = new DesktopPeerConnectionService(scopeFactory, _currentPersonaMock.Object, _fakeStreamRegistry, _fakeTimeProvider);

        var result = await sut.ConnectToPeerAsync(PublicIdentityId.New());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NO_ACTIVE_PERSONA");
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_WhenNoActivePersonaExists_ReturnsFailure()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var sut = new DesktopPeerConnectionService(scopeFactory, _currentPersonaMock.Object, _fakeStreamRegistry, _fakeTimeProvider);

        var routingToken = BlindedRoutingToken.New();
        using var deliveryToken = DeliveryToken.GenerateRandom();

        var result = await sut.ConnectToDirectPeerAsync(
            PublicIdentityId.New(),
            routingToken,
            deliveryToken,
            new Uri("https://127.0.0.1:5100"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NO_ACTIVE_PERSONA");
    }
}

internal sealed class FakeDesktopPersonaDirectory : IPersonaDirectory
{
    public List<PersonaDirectoryEntry> Personas { get; } = new();

    public Task<IReadOnlyList<PersonaDirectoryEntry>> ListAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PersonaDirectoryEntry>>(Personas);
    }

    public Task<PersonaDirectoryEntry> CreateAsync(PublicIdentityId identityId, string displayName, int preferredPort, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task RenameAsync(PublicIdentityId identityId, string displayName, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task ShredAndDeleteAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}

internal sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
