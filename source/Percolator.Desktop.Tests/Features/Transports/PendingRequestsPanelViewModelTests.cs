// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class PendingRequestsPanelViewModelTests
{
    private FakeInboundRequestStateService _fakeInboundService = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _requesterId;

    [SetUp]
    public void SetUp()
    {
        _fakeInboundService = new FakeInboundRequestStateService();
        _ownerId = PublicIdentityId.New();
        _requesterId = PublicIdentityId.New();
    }

    [TearDown]
    public void TearDown()
    {
        _fakeInboundService.Dispose();
    }

    private PendingRequestsPanelViewModel CreateViewModel(PublicIdentityId? identityId = null)
    {
        var contextMock = new Mock<IDialogContext<PublicIdentityId>>();
        contextMock.SetupGet(c => c.Data).Returns(identityId ?? _ownerId);

        return new PendingRequestsPanelViewModel(
            contextMock.Object,
            _fakeInboundService,
            NullLogger<PendingRequestsPanelViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<PendingRequestsPanelViewModel>.Instance;
        var contextMock = new Mock<IDialogContext<PublicIdentityId>>();
        contextMock.SetupGet(c => c.Data).Returns(_ownerId);

        Action act0 = () => new PendingRequestsPanelViewModel(null!, _fakeInboundService, logger);
        Action act1 = () => new PendingRequestsPanelViewModel(contextMock.Object, null!, logger);
        Action act2 = () => new PendingRequestsPanelViewModel(contextMock.Object, _fakeInboundService, null!);

        act0.Should().Throw<ArgumentNullException>();
        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadRequestsAsync_PopulatesPendingRequests_ReflectedFromStateService()
    {
        var request = new InboundPendingRequestModel(
            _requesterId, "Alice Pending", "abcd...1234", DateTimeOffset.UtcNow);
        _fakeInboundService.BackingList.Add(request);

        using var vm = CreateViewModel();
        await vm.InitializeAsync(_ownerId);

        _fakeInboundService.LoadedPersonas.Should().Contain(_ownerId);
        vm.Requests.Should().ContainSingle();
        vm.Requests.First().RequesterId.Should().Be(_requesterId);
        vm.Requests.First().DisplayName.Should().Be("Alice Pending");
        vm.HasRequests.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse();
        vm.RequestCount.Should().Be(1);
    }

    [Test]
    public async Task LoadRequestsAsync_WhenEmpty_SetsIsEmptyTrueAndHasRequestsFalse()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync(_ownerId);

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.IsEmpty.Should().BeTrue();
        vm.RequestCount.Should().Be(0);
    }

    [Test]
    public async Task AcceptRequestAsync_DelegatesToStateService_AndSetsStatusMessage()
    {
        var request = new InboundPendingRequestModel(
            _requesterId, "Alice", "abcd...1234", DateTimeOffset.UtcNow);
        _fakeInboundService.BackingList.Add(request);

        using var vm = CreateViewModel();
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.AcceptRequestAsync(item);

        _fakeInboundService.Accepted.Should().ContainSingle();
        _fakeInboundService.Accepted[0].Should().Be((_ownerId, _requesterId));

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Alice");
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task RejectRequestAsync_DelegatesToStateService_AndSetsStatusMessage()
    {
        var request = new InboundPendingRequestModel(
            _requesterId, "Bob", "abcd...1234", DateTimeOffset.UtcNow);
        _fakeInboundService.BackingList.Add(request);

        using var vm = CreateViewModel();
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.RejectRequestAsync(item);

        _fakeInboundService.Rejected.Should().ContainSingle();
        _fakeInboundService.Rejected[0].Should().Be((_ownerId, _requesterId));

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Bob");
    }

    [Test]
    public async Task BlockRequestAsync_DelegatesToStateService_AndSetsStatusMessage()
    {
        var request = new InboundPendingRequestModel(
            _requesterId, "Eve Spammer", "abcd...1234", DateTimeOffset.UtcNow);
        _fakeInboundService.BackingList.Add(request);

        using var vm = CreateViewModel();
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.BlockRequestAsync(item);

        _fakeInboundService.Blocked.Should().ContainSingle();
        _fakeInboundService.Blocked[0].Should().Be((_ownerId, _requesterId));

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Eve Spammer");
        vm.StatusMessage.Should().Contain("Future packets will be silently dropped");
    }
}

internal sealed class FakeInboundRequestStateService : IInboundRequestStateService
{
    private readonly ObservableList<InboundPendingRequestModel> _list = new();
    private readonly ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> _view;
    private readonly ReactiveProperty<int> _count = new(0);

    public FakeInboundRequestStateService()
    {
        _view = _list.CreateView(x => x);
        _view.ObserveCountChanged(true).Subscribe(c => _count.Value = c);
    }

    public ReadOnlyReactiveProperty<int> PendingCount => _count;
    public ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> Requests => _view;
    public ObservableList<InboundPendingRequestModel> BackingList => _list;

    public List<PublicIdentityId> LoadedPersonas { get; } = new();
    public List<(PublicIdentityId Persona, PublicIdentityId Requester)> Accepted { get; } = new();
    public List<(PublicIdentityId Persona, PublicIdentityId Requester)> Rejected { get; } = new();
    public List<(PublicIdentityId Persona, PublicIdentityId Requester)> Blocked { get; } = new();

    public Task LoadRequestsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
    {
        LoadedPersonas.Add(personaId);
        return Task.CompletedTask;
    }

    public Task AcceptRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        Accepted.Add((personaId, requesterId));
        var item = _list.FirstOrDefault(r => r.RequesterId == requesterId);
        if (item != null) _list.Remove(item);
        return Task.CompletedTask;
    }

    public Task RejectRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        Rejected.Add((personaId, requesterId));
        var item = _list.FirstOrDefault(r => r.RequesterId == requesterId);
        if (item != null) _list.Remove(item);
        return Task.CompletedTask;
    }

    public Task BlockRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        Blocked.Add((personaId, requesterId));
        var item = _list.FirstOrDefault(r => r.RequesterId == requesterId);
        if (item != null) _list.Remove(item);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _view.Dispose();
        _count.Dispose();
    }
}
