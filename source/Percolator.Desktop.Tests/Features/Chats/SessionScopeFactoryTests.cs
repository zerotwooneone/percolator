// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Apps.Chat.Features.Introductions;
using R3;

[TestFixture]
public sealed class SessionScopeFactoryTests
{
    private ServiceProvider _serviceProvider = null!;
    private Mock<ICurrentPersonaService> _personaServiceMock = null!;
    private Subject<ActivePersonaModel?> _personaChangedSubject = null!;
    private SessionScopeFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _personaChangedSubject = new Subject<ActivePersonaModel?>();
        _personaServiceMock = new Mock<ICurrentPersonaService>();
        _personaServiceMock.Setup(p => p.CurrentPersona)
            .Returns(new ActivePersonaModel(PublicIdentityId.New(), "Alice", 5124));
        _personaServiceMock.Setup(p => p.CurrentPersonaChanged)
            .Returns(_personaChangedSubject);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICurrentPersonaService>(_personaServiceMock.Object);
        services.AddSingleton(Mock.Of<IChatService>());

        var queryMock = new Mock<IChatMessageQueryService>();
        queryMock.Setup(q => q.GetMessagesAsync(It.IsAny<ChannelId>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<ulong>(), default))
            .ReturnsAsync(Array.Empty<ChatMessageReadModel>());
        services.AddSingleton(queryMock.Object);

        services.AddSingleton<IConversationStateService>(new FakeConversationStateService());
        services.AddSingleton<IChatDraftStateService>(new ChatDraftStateService(_personaServiceMock.Object, NullLogger<ChatDraftStateService>.Instance));
        services.AddSingleton<ILogger<ChatViewModel>>(NullLogger<ChatViewModel>.Instance);
        services.AddSingleton<ILogger<ChatReloadCoordinator>>(NullLogger<ChatReloadCoordinator>.Instance);
        services.AddSingleton<ILogger<SessionScopeFactory>>(NullLogger<SessionScopeFactory>.Instance);
        services.AddSingleton(Mock.Of<IFileTransferActionHandler>());
        services.AddSingleton(Mock.Of<IFileTransferQueryService>());
        services.AddSingleton(Mock.Of<IDialogService>());
        services.AddSingleton(Mock.Of<IFilePickerService>());
        services.AddSingleton(Mock.Of<IPeerIntroductionConsentRepository>());

        _serviceProvider = services.BuildServiceProvider();
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        _factory = new SessionScopeFactory(
            scopeFactory,
            _personaServiceMock.Object,
            NullLogger<SessionScopeFactory>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
        _personaChangedSubject.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    public void GetOrCreateChatViewModel_ReturnsViewModel_AndCachesInstance()
    {
        var channelId = ChannelId.New();
        var vm1 = _factory.GetOrCreateChatViewModel(channelId);
        var vm2 = _factory.GetOrCreateChatViewModel(channelId);

        vm1.Should().NotBeNull();
        vm2.Should().BeSameAs(vm1);
    }

    [Test]
    public void GetOrCreateChatViewModel_WhenExceedingCapacity5_EvictsLeastRecentlyUsed()
    {
        var channels = new List<ChannelId>();
        var vms = new List<ChatViewModel>();

        // Create 5 chats
        for (int i = 0; i < 5; i++)
        {
            var cid = ChannelId.New();
            channels.Add(cid);
            vms.Add(_factory.GetOrCreateChatViewModel(cid));
        }

        // Access channel 0 again to make it MRU
        _factory.GetOrCreateChatViewModel(channels[0]);

        // Add 6th chat; channel 1 (which was least recently used) should be evicted
        var channel6 = ChannelId.New();
        var vm6 = _factory.GetOrCreateChatViewModel(channel6);

        // When retrieving channel 1 again, it should be re-instantiated (new instance)
        var newVm1 = _factory.GetOrCreateChatViewModel(channels[1]);
        newVm1.Should().NotBeSameAs(vms[1]);

        // When retrieving channel 0, it should still be cached
        var cachedVm0 = _factory.GetOrCreateChatViewModel(channels[0]);
        cachedVm0.Should().BeSameAs(vms[0]);
    }

    [Test]
    public void PersonaChanged_ClearsAllCachedScopes()
    {
        var channelId = ChannelId.New();
        var vm1 = _factory.GetOrCreateChatViewModel(channelId);

        // Persona change triggers ClearAll()
        _personaChangedSubject.OnNext(new ActivePersonaModel(PublicIdentityId.New(), "Bob", 5126));

        var vm2 = _factory.GetOrCreateChatViewModel(channelId);
        vm2.Should().NotBeSameAs(vm1);
    }

    [Test]
    public void EvictChat_RemovesSpecificChannelScope()
    {
        var channelId = ChannelId.New();
        var vm1 = _factory.GetOrCreateChatViewModel(channelId);

        _factory.EvictChat(channelId);

        var vm2 = _factory.GetOrCreateChatViewModel(channelId);
        vm2.Should().NotBeSameAs(vm1);
    }
}
