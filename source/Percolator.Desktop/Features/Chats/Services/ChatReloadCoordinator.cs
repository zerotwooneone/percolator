// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using R3;

/// <summary>
/// Debounced conversation reload coordinator implementing <c>DEC-DEBOUNCED-DROP-RELOAD</c>.
/// Inbound database update triggers arriving in bursts (such as swarm payload flushes)
/// are debounced by 50ms and executed with <see cref="AwaitOperation.Drop"/> to eliminate
/// SQLite lock contention and prevent UI frame rate stutter.
/// </summary>
public sealed class ChatReloadCoordinator : IDisposable
{
    private readonly ChannelId _channelId;
    private readonly IChatMessageQueryService _queryService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatReloadCoordinator> _logger;

    private readonly Subject<Unit> _reloadTrigger = new();
    private readonly Subject<IReadOnlyList<ChatMessageReadModel>> _messagesLoaded = new();
    private DisposableBag _bag;
    private bool _disposed;

    public const int DefaultPageSize = 100;

    public ChatReloadCoordinator(
        ChannelId channelId,
        IChatMessageQueryService queryService,
        TimeProvider timeProvider,
        ILogger<ChatReloadCoordinator> logger)
        : this(channelId, queryService, timeProvider, logger, DefaultPageSize)
    {
    }

    public ChatReloadCoordinator(
        ChannelId channelId,
        IChatMessageQueryService queryService,
        TimeProvider timeProvider,
        ILogger<ChatReloadCoordinator> logger,
        int defaultPageSize)
    {
        _channelId = channelId;
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(50), _timeProvider)
            .SelectAwait(async (_, ct) =>
            {
                try
                {
                    var nowMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    return await _queryService.GetMessagesAsync(
                        _channelId,
                        beforeTimestampMs: null,
                        limit: defaultPageSize,
                        currentTimestampMs: nowMs,
                        ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (IReadOnlyList<ChatMessageReadModel>)Array.Empty<ChatMessageReadModel>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reloading messages for channel {ChannelId}", _channelId);
                    return (IReadOnlyList<ChatMessageReadModel>)Array.Empty<ChatMessageReadModel>();
                }
            }, AwaitOperation.Drop)
            .Subscribe(msgs =>
            {
                _messagesLoaded.OnNext(msgs);
            })
            .AddTo(ref _bag);
    }

    /// <summary>
    /// Stream of reloaded message batches emitted after debounced database queries.
    /// </summary>
    public Observable<IReadOnlyList<ChatMessageReadModel>> MessagesLoaded => _messagesLoaded;

    /// <summary>
    /// Signals a reload trigger into the debounced pipeline. Redundant signals arriving
    /// within 50ms or while a query is in-flight will be safely dropped.
    /// </summary>
    public void TriggerReload()
    {
        if (_disposed) return;
        _reloadTrigger.OnNext(Unit.Default);
    }

    /// <summary>
    /// Directly executes an immediate, non-debounced read from SQLite (e.g. for initial view load).
    /// </summary>
    public async Task<IReadOnlyList<ChatMessageReadModel>> ReloadImmediateAsync(int limit = 100, CancellationToken ct = default)
    {
        if (_disposed) return Array.Empty<ChatMessageReadModel>();

        var nowMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var messages = await _queryService.GetMessagesAsync(
            _channelId,
            beforeTimestampMs: null,
            limit: limit,
            currentTimestampMs: nowMs,
            ct: ct).ConfigureAwait(false);

        _messagesLoaded.OnNext(messages);
        return messages;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bag.Dispose();
        _reloadTrigger.OnCompleted();
        _reloadTrigger.Dispose();
        _messagesLoaded.OnCompleted();
        _messagesLoaded.Dispose();
    }
}
