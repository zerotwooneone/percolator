using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.IdentityGate.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Tests.IdentityGate;

[TestFixture]
public sealed class IdentityGateViewModelTests
{
    private DesktopIdentityProfileRepository _profileRepo = null!;
    private DesktopLocalIdentityKeyStore _keyStore = null!;
    private NetworkStealthGate _stealthGate = null!;
    private Mock<IWindowManager> _mockWindowManager = null!;
    private Mock<IDateTimeProvider> _mockTimeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _profileRepo = new DesktopIdentityProfileRepository();
        _keyStore = new DesktopLocalIdentityKeyStore();
        _stealthGate = new NetworkStealthGate(NullLogger<NetworkStealthGate>.Instance);
        _mockWindowManager = new Mock<IWindowManager>();
        _mockTimeProvider = new Mock<IDateTimeProvider>();
        _mockTimeProvider.Setup(t => t.UtcNow).Returns(DateTimeOffset.UtcNow);
    }

    [TearDown]
    public void TearDown()
    {
        _stealthGate.Dispose();
    }

    private IdentityGateViewModel CreateViewModel()
    {
        return new IdentityGateViewModel(
            _profileRepo,
            _keyStore,
            _stealthGate,
            _mockWindowManager.Object,
            _mockTimeProvider.Object,
            NullLogger<IdentityGateViewModel>.Instance);
    }

    [Test]
    public async Task InitializeAsync_LoadsExistingIdentities_AndAutoSelectsFirst()
    {
        var id1 = PublicIdentityId.New();
        var id2 = PublicIdentityId.New();

        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMinutes(1);
        _mockTimeProvider.SetupSequence(t => t.UtcNow).Returns(t1).Returns(t2).Returns(t2);

        var p1 = IdentityProfile.Create(id1, "Alice", IdentityRole.UserPersona, _mockTimeProvider.Object).Value!;
        var p2 = IdentityProfile.Create(id2, "Bob", IdentityRole.UserPersona, _mockTimeProvider.Object).Value!;

        await _profileRepo.SaveAsync(p1);
        await _profileRepo.SaveAsync(p2);

        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.Identities.Should().HaveCount(2);
        vm.Identities[0].DisplayName.Should().Be("Alice");
        vm.Identities[0].IsSelected.Should().BeTrue();
        vm.CanActivate.Should().BeTrue();
    }

    [Test]
    public async Task CanActivate_IsFalse_WhenAllIdentitiesDeselected()
    {
        var id = PublicIdentityId.New();
        var profile = IdentityProfile.Create(id, "Alice", IdentityRole.UserPersona, _mockTimeProvider.Object).Value!;
        await _profileRepo.SaveAsync(profile);

        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.CanActivate.Should().BeTrue();

        // Deselect
        vm.Identities[0].IsSelected = false;
        vm.CanActivate.Should().BeFalse();
    }

    [Test]
    public async Task ConfirmCreateIdentityAsync_GeneratesKeys_SavesProfile_AndSelectsNewIdentity()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.NewDisplayName = "Charlie Decentralized";
        await vm.ConfirmCreateIdentityCommand.ExecuteAsync(null);

        vm.Identities.Should().ContainSingle(i => i.DisplayName == "Charlie Decentralized");
        var created = vm.Identities.First(i => i.DisplayName == "Charlie Decentralized");
        created.IsSelected.Should().BeTrue();
        vm.CanActivate.Should().BeTrue();

        // Verify key store has the generated keys
        var pubKey = await _keyStore.GetIdentityPublicKeyAsync(created.Id);
        pubKey.Should().NotBeNull();
        var privKey = await _keyStore.GetIdentityPrivateKeyAsync(created.Id);
        privKey.Should().NotBeNull().And.HaveCount(32);
    }

    [Test]
    public async Task ActivateSessionAsync_ExitsStealthMode_AndSwitchesToMainWindow()
    {
        var id = PublicIdentityId.New();
        var profile = IdentityProfile.Create(id, "Alice", IdentityRole.UserPersona, _mockTimeProvider.Object).Value!;
        await _profileRepo.SaveAsync(profile);

        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.ActivateSessionCommand.ExecuteAsync(null);

        _stealthGate.IsStealthActive.Should().BeFalse("session activation must exit stealth mode");
        _stealthGate.ActiveIdentities.Should().Contain(id);
        _mockWindowManager.Verify(w => w.SwitchToMainWindow(), Times.Once);
    }
}
