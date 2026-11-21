// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Thread-safe in-memory store for volatile conversation drafts ([UISS-003], [UISS-007]).
/// </summary>
public sealed class ChatDraftStateService : IChatDraftStateService
{
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<ChatDraftStateService> _logger;

    private readonly Dictionary<(PublicIdentityId PersonaId, ChannelId ChannelId), ChatDraftModel> _drafts = new();
    private readonly Subject<ChannelId> _draftChanged = new();
    private readonly object _lock = new();

    private DisposableBag _bag;
    private bool _disposed;

    public ChatDraftStateService(
        ICurrentPersonaService currentPersonaService,
        ILogger<ChatDraftStateService> logger)
    {
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentPersonaService.CurrentPersonaChanged
            .Subscribe(newPersona =>
            {
                if (newPersona == null)
                {
                    // Session locked or persona deselected: zeroize all drafts in memory
                    lock (_lock)
                    {
                        _drafts.Clear();
                    }
                }
            })
            .AddTo(ref _bag);
    }

    public Observable<ChannelId> DraftChanged => _draftChanged;

    public ChatDraftModel GetDraft(PublicIdentityId personaId, ChannelId channelId)
    {
        lock (_lock)
        {
            if (_drafts.TryGetValue((personaId, channelId), out var draft))
            {
                return draft;
            }

            return new ChatDraftModel(string.Empty, 0, Array.Empty<string>(), null);
        }
    }

    public void SaveDraft(PublicIdentityId personaId, ChannelId channelId, ChatDraftModel draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (_disposed || !personaId.IsValid) return;

        lock (_lock)
        {
            if (string.IsNullOrEmpty(draft.Text) && draft.Attachments.Count == 0 && draft.ExpireAfterSeconds == null)
            {
                _drafts.Remove((personaId, channelId));
            }
            else
            {
                _drafts[(personaId, channelId)] = draft;
            }
        }

        // Notify outside lock ([UISS-004])
        _draftChanged.OnNext(channelId);
    }

    public void ClearDraft(PublicIdentityId personaId, ChannelId channelId)
    {
        if (_disposed || !personaId.IsValid) return;

        var removed = false;
        lock (_lock)
        {
            removed = _drafts.Remove((personaId, channelId));
        }

        if (removed)
        {
            _draftChanged.OnNext(channelId);
        }
    }

    public void ClearAllForPersona(PublicIdentityId personaId)
    {
        if (_disposed || !personaId.IsValid) return;

        List<ChannelId> removedChannels = new();
        lock (_lock)
        {
            var keysToRemove = new List<(PublicIdentityId PersonaId, ChannelId ChannelId)>();
            foreach (var key in _drafts.Keys)
            {
                if (key.PersonaId == personaId)
                {
                    keysToRemove.Add(key);
                    removedChannels.Add(key.ChannelId);
                }
            }

            foreach (var key in keysToRemove)
            {
                _drafts.Remove(key);
            }
        }

        foreach (var ch in removedChannels)
        {
            _draftChanged.OnNext(ch);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _drafts.Clear();
        }

        _bag.Dispose();
        _draftChanged.OnCompleted();
        _draftChanged.Dispose();
    }
}
