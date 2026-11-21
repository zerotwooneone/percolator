// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

[TestFixture]
public sealed class ChatDraftStateServiceTests
{
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private ChatDraftStateService _service = null!;
    private PublicIdentityId _personaA;
    private PublicIdentityId _personaB;
    private ChannelId _channel1;
    private ChannelId _channel2;

    [SetUp]
    public void SetUp()
    {
        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _currentPersonaServiceMock.Setup(p => p.CurrentPersonaChanged)
            .Returns(Observable.Empty<ActivePersonaModel?>());

        _service = new ChatDraftStateService(_currentPersonaServiceMock.Object, NullLogger<ChatDraftStateService>.Instance);
        _personaA = PublicIdentityId.New();
        _personaB = PublicIdentityId.New();
        _channel1 = ChannelId.New();
        _channel2 = ChannelId.New();
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
    }

    [Test]
    public void GetDraft_WhenUnset_ReturnsEmptyDraft()
    {
        var draft = _service.GetDraft(_personaA, _channel1);
        draft.Should().NotBeNull();
        draft.Text.Should().BeEmpty();
        draft.CursorPosition.Should().Be(0);
        draft.Attachments.Should().BeEmpty();
        draft.ExpireAfterSeconds.Should().BeNull();
    }

    [Test]
    public void SaveDraft_AndGetDraft_PersistsInRAM()
    {
        var model = new ChatDraftModel("Hello world", 5, new[] { "file.pdf" }, 300);
        _service.SaveDraft(_personaA, _channel1, model);

        var retrieved = _service.GetDraft(_personaA, _channel1);
        retrieved.Text.Should().Be("Hello world");
        retrieved.CursorPosition.Should().Be(5);
        retrieved.Attachments.Should().ContainSingle().Which.Should().Be("file.pdf");
        retrieved.ExpireAfterSeconds.Should().Be(300);
    }

    [Test]
    public void Drafts_AreIsolatedAcrossPersonas()
    {
        var draftA = new ChatDraftModel("Persona A secret", 10, Array.Empty<string>(), null);
        var draftB = new ChatDraftModel("Persona B message", 2, Array.Empty<string>(), 60);

        _service.SaveDraft(_personaA, _channel1, draftA);
        _service.SaveDraft(_personaB, _channel1, draftB);

        _service.GetDraft(_personaA, _channel1).Text.Should().Be("Persona A secret");
        _service.GetDraft(_personaB, _channel1).Text.Should().Be("Persona B message");
    }

    [Test]
    public void ClearDraft_RemovesDraft_AndEmitsNotification()
    {
        var emitted = new List<ChannelId>();
        using var sub = _service.DraftChanged.Subscribe(item => emitted.Add(item));

        var model = new ChatDraftModel("To be cleared", 3, Array.Empty<string>(), null);
        _service.SaveDraft(_personaA, _channel1, model);
        emitted.Should().HaveCount(1);
        emitted[0].Should().Be(_channel1);

        _service.ClearDraft(_personaA, _channel1);

        _service.GetDraft(_personaA, _channel1).Text.Should().BeEmpty();
        emitted.Should().HaveCount(2);
        emitted[1].Should().Be(_channel1);
    }

    [Test]
    public void ClearAllForPersona_RemovesOnlyTargetPersonaDrafts()
    {
        _service.SaveDraft(_personaA, _channel1, new ChatDraftModel("Draft A1", 0, Array.Empty<string>(), null));
        _service.SaveDraft(_personaA, _channel2, new ChatDraftModel("Draft A2", 0, Array.Empty<string>(), null));
        _service.SaveDraft(_personaB, _channel1, new ChatDraftModel("Draft B1", 0, Array.Empty<string>(), null));

        _service.ClearAllForPersona(_personaA);

        _service.GetDraft(_personaA, _channel1).Text.Should().BeEmpty();
        _service.GetDraft(_personaA, _channel2).Text.Should().BeEmpty();
        _service.GetDraft(_personaB, _channel1).Text.Should().Be("Draft B1");
    }

    [Test]
    public void CurrentPersonaChanged_WhenNull_ZeroizesAllDraftsInMemory()
    {
        var personaSubject = new Subject<ActivePersonaModel?>();
        _currentPersonaServiceMock.Setup(p => p.CurrentPersonaChanged)
            .Returns(personaSubject);

        using var service = new ChatDraftStateService(_currentPersonaServiceMock.Object, NullLogger<ChatDraftStateService>.Instance);

        service.SaveDraft(_personaA, _channel1, new ChatDraftModel("Draft A1", 0, Array.Empty<string>(), null));
        service.SaveDraft(_personaB, _channel2, new ChatDraftModel("Draft B2", 0, Array.Empty<string>(), null));

        service.GetDraft(_personaA, _channel1).Text.Should().Be("Draft A1");
        service.GetDraft(_personaB, _channel2).Text.Should().Be("Draft B2");

        // Persona deselected or session locked: zeroize all drafts in memory
        personaSubject.OnNext(null);

        service.GetDraft(_personaA, _channel1).Text.Should().BeEmpty();
        service.GetDraft(_personaB, _channel2).Text.Should().BeEmpty();
    }
}

