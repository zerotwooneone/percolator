// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using R3;

/// <summary>
/// Session-scoped factory managing bounded LRU-cached child DI scopes and ViewModels
/// for active conversations ([DEC-LRU-SESSION-SCOPES], [UISS-007]).
/// </summary>
public sealed class SessionScopeFactory : ISessionScopeFactory
{
    private const int MaxCachedScopes = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<SessionScopeFactory> _logger;
    private readonly IDisposable _personaSubscription;

    private readonly object _lock = new();
    private readonly Dictionary<ChannelId, (IServiceScope Scope, ChatViewModel ViewModel, LinkedListNode<ChannelId> Node)> _cache = new();
    private readonly LinkedList<ChannelId> _lruList = new();
    private bool _isDisposed;

    public SessionScopeFactory(
        IServiceScopeFactory scopeFactory,
        ICurrentPersonaService currentPersonaService,
        ILogger<SessionScopeFactory> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _personaSubscription = _currentPersonaService.CurrentPersonaChanged
            .Subscribe(_ => ClearAll());
    }

    public ChatViewModel GetOrCreateChatViewModel(ChannelId channelId)
    {
        (IServiceScope Scope, ChatViewModel ViewModel)? evicted = null;

        lock (_lock)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(SessionScopeFactory));
            }

            if (_cache.TryGetValue(channelId, out var entry))
            {
                // Move to MRU (front of list)
                _lruList.Remove(entry.Node);
                _lruList.AddFirst(entry.Node);
                return entry.ViewModel;
            }

            // Evict LRU if at or above capacity
            if (_cache.Count >= MaxCachedScopes)
            {
                var oldestNode = _lruList.Last;
                if (oldestNode != null)
                {
                    var oldestChannelId = oldestNode.Value;
                    if (_cache.TryGetValue(oldestChannelId, out var toEvict))
                    {
                        _cache.Remove(oldestChannelId);
                        _lruList.RemoveLast();
                        evicted = (toEvict.Scope, toEvict.ViewModel);
                        _logger.LogDebug("Evicting LRU chat session for channel {ChannelId}", oldestChannelId);
                    }
                }
            }

            var scope = _scopeFactory.CreateScope();
            ChatViewModel viewModel;
            try
            {
                viewModel = ActivatorUtilities.CreateInstance<ChatViewModel>(scope.ServiceProvider, channelId);
            }
            catch (Exception ex)
            {
                scope.Dispose();
                _logger.LogError(ex, "Failed to instantiate ChatViewModel for channel {ChannelId}", channelId);
                throw;
            }

            var node = new LinkedListNode<ChannelId>(channelId);
            _lruList.AddFirst(node);
            _cache[channelId] = (scope, viewModel, node);

            // Dispose evicted scope out of critical path later
            if (evicted != null)
            {
                DisposeScopeEntry(evicted.Value.Scope, evicted.Value.ViewModel);
            }

            return viewModel;
        }
    }

    public void EvictChat(ChannelId channelId)
    {
        (IServiceScope Scope, ChatViewModel ViewModel)? toDispose = null;

        lock (_lock)
        {
            if (_cache.TryGetValue(channelId, out var entry))
            {
                _cache.Remove(channelId);
                _lruList.Remove(entry.Node);
                toDispose = (entry.Scope, entry.ViewModel);
            }
        }

        if (toDispose != null)
        {
            DisposeScopeEntry(toDispose.Value.Scope, toDispose.Value.ViewModel);
        }
    }

    public void ClearAll()
    {
        List<(IServiceScope Scope, ChatViewModel ViewModel)> toDispose;

        lock (_lock)
        {
            toDispose = new List<(IServiceScope Scope, ChatViewModel ViewModel)>(_cache.Count);
            foreach (var kvp in _cache.Values)
            {
                toDispose.Add((kvp.Scope, kvp.ViewModel));
            }

            _cache.Clear();
            _lruList.Clear();
        }

        foreach (var (scope, vm) in toDispose)
        {
            DisposeScopeEntry(scope, vm);
        }
    }

    private void DisposeScopeEntry(IServiceScope scope, ChatViewModel viewModel)
    {
        try
        {
            viewModel.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing ChatViewModel for channel {ChannelId}", viewModel.ChannelId);
        }

        try
        {
            scope.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing child DI scope for channel {ChannelId}", viewModel.ChannelId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        _personaSubscription.Dispose();
        ClearAll();
    }
}
