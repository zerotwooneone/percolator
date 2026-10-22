using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Tests.Shell;

[TestFixture]
public sealed class MainWindowViewModelTests
{
    private NetworkStealthGate _stealthGate = null!;
    private Mock<IWindowManager> _mockWindowManager = null!;

    [SetUp]
    public void SetUp()
    {
        _stealthGate = new NetworkStealthGate(NullLogger<NetworkStealthGate>.Instance);
        _mockWindowManager = new Mock<IWindowManager>();
    }

    [TearDown]
    public void TearDown()
    {
        _stealthGate.Dispose();
    }

    [Test]
    public async Task UpdatesActiveIdentitiesCount_OnStealthGateChanges()
    {
        var id1 = PublicIdentityId.New();
        var id2 = PublicIdentityId.New();

        var vm = new MainWindowViewModel(_stealthGate, _mockWindowManager.Object);
        vm.ActiveIdentitiesCount.Should().Be(0);

        await _stealthGate.ActivateAsync([id1, id2]);
        vm.ActiveIdentitiesCount.Should().Be(2);
        vm.StatusText.Should().Contain("Network Active (2 Identities)");

        await _stealthGate.DeactivateAsync();
        vm.ActiveIdentitiesCount.Should().Be(0);
        vm.StatusText.Should().Contain("Stealth Mode");
    }

    [Test]
    public async Task LockSessionAsync_DeactivatesNetwork_AndSwitchesToIdentityGate()
    {
        var id = PublicIdentityId.New();
        await _stealthGate.ActivateAsync([id]);

        var vm = new MainWindowViewModel(_stealthGate, _mockWindowManager.Object);

        await vm.LockSessionCommand.ExecuteAsync(null);

        _stealthGate.IsStealthActive.Should().BeTrue();
        _mockWindowManager.Verify(w => w.SwitchToIdentityGate(), Times.Once);
    }
}
