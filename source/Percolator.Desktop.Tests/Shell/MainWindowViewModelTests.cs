// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Desktop.Features.Shell.ViewModels;

namespace Percolator.Desktop.Tests.Shell;

[TestFixture]
public sealed class MainWindowViewModelTests
{
    // The pending-requests indicator light binds HasPendingRequests, which projects
    // PendingRequestCount > 0. The load-bearing contract is that PropertyChanged fires for
    // BOTH properties when the count crosses the zero boundary — without
    // [NotifyPropertyChangedFor] the indicator silently breaks even though the getter stays correct.
    [Test]
    public void PendingRequestCount_NotifiesHasPendingRequests_WhenCrossingZeroBoundary()
    {
        var vm = new MainWindowViewModel();
        vm.HasPendingRequests.Should().BeFalse();

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.PendingRequestCount = 2;
        vm.HasPendingRequests.Should().BeTrue("the indicator light must show staged inbound handshakes");
        notified.Should().Contain(nameof(MainWindowViewModel.HasPendingRequests),
            "the indicator binding must be notified when the count leaves zero");

        notified.Clear();
        vm.PendingRequestCount = 0;
        vm.HasPendingRequests.Should().BeFalse();
        notified.Should().Contain(nameof(MainWindowViewModel.HasPendingRequests),
            "the indicator binding must be notified when the count returns to zero");
    }
}
