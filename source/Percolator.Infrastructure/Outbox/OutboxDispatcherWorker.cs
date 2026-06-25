using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Events;
using Percolator.Infrastructure.Persistence;
using Percolator.Application.Network;
using Percolator.Chat.Messaging.ValueObjects;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Percolator.Infrastructure.Outbox;

/// <summary>
/// Background worker that processes domain events from the outbox and dispatches them.
/// Handles group provisioning requests and member invitations with transient network backoff.
/// </summary>
public sealed class OutboxDispatcherWorker : BackgroundService
{
    private readonly PercolatorDbContext _db;
    private readonly IRemoteEnvelopeSender _remoteEnvelopeSender;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);

    public OutboxDispatcherWorker(
        PercolatorDbContext db,
        IRemoteEnvelopeSender remoteEnvelopeSender,
        ILogger<OutboxDispatcherWorker> logger)
    {
        _db = db;
        _remoteEnvelopeSender = remoteEnvelopeSender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxDispatcherWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("OutboxDispatcherWorker stopped");
    }

    private async Task ProcessOutboxAsync(CancellationToken ct)
    {
        var unprocessedEvents = await _db.RelayOutbox
            .Where(e => e.ProcessedAtUtc == null)
            .OrderBy(e => e.Id)
            .Take(10)
            .ToListAsync(ct);

        if (unprocessedEvents.Count == 0)
        {
            return;
        }

        foreach (var outboxItem in unprocessedEvents)
        {
            await ProcessOutboxItemAsync(outboxItem, ct);
        }
    }

    private async Task ProcessOutboxItemAsync(RelayOutboxDbo outboxItem, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxAttempts = 5;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            try
            {
                Percolator.Chat.SeedWork.IDomainEvent? domainEvent = outboxItem.EventType switch
                {
                    nameof(GroupProvisioningRequestedDomainEvent) => JsonSerializer.Deserialize<GroupProvisioningRequestedDomainEvent>(outboxItem.PayloadJson),
                    nameof(MemberInvitedDomainEvent) => JsonSerializer.Deserialize<MemberInvitedDomainEvent>(outboxItem.PayloadJson),
                    _ => throw new InvalidOperationException($"Unknown event type: {outboxItem.EventType}")
                };

                if (domainEvent is null)
                {
                    _logger.LogWarning("Failed to deserialize event {EventType} with payload {Payload}", outboxItem.EventType, outboxItem.PayloadJson);
                    MarkAsProcessed(outboxItem);
                    return;
                }

                await DispatchEventAsync(domainEvent, outboxItem.DestinationPkh, ct);

                // Success - mark as processed
                MarkAsProcessed(outboxItem);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Successfully processed outbox item {Id} of type {EventType}", outboxItem.Id, outboxItem.EventType);
                return;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex))
            {
                attempt++;
                _logger.LogWarning(ex, "Transient network error processing outbox item {Id} (attempt {Attempt}/{MaxAttempts})", outboxItem.Id, attempt, maxAttempts);
                
                if (attempt < maxAttempts)
                {
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _maxBackoff.Ticks));
                }
                else
                {
                    _logger.LogError(ex, "Max attempts reached for outbox item {Id}", outboxItem.Id);
                    // Don't mark as processed - will be retried on next poll
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Non-transient error processing outbox item {Id}", outboxItem.Id);
                // Mark as processed to avoid infinite retry loops for permanent errors
                MarkAsProcessed(outboxItem);
                await _db.SaveChangesAsync(ct);
                return;
            }
        }
    }

    private async Task DispatchEventAsync(Percolator.Chat.SeedWork.IDomainEvent domainEvent, Pkh destinationPkh, CancellationToken ct)
    {
        switch (domainEvent)
        {
            case GroupProvisioningRequestedDomainEvent provisioningEvent:
                // TODO: Invoke Relay's ProvisionGroup gRPC endpoint
                // This requires the relay client and orchestrator setup
                _logger.LogInformation("GroupProvisioningRequestedDomainEvent for conversation {ConversationId} - relay provisioning not yet implemented", provisioningEvent.ConversationId);
                break;

            case MemberInvitedDomainEvent inviteEvent:
                // Dispatch GroupInvite via IRemoteEnvelopeSender
                // TODO: Create ChatEnvelope with GroupInvite message
                _logger.LogInformation("MemberInvitedDomainEvent for conversation {ConversationId} to participant {Pkh} - invite dispatch not yet implemented", inviteEvent.ConversationId, inviteEvent.ParticipantId.Pkh);
                break;

            default:
                throw new InvalidOperationException($"Unknown domain event type: {domainEvent.GetType().Name}");
        }
    }

    private void MarkAsProcessed(RelayOutboxDbo outboxItem)
    {
        outboxItem.ProcessedAtUtc = DateTimeOffset.UtcNow;
    }

    private static bool IsTransientNetworkError(Exception ex)
    {
        // Check for common transient network error patterns
        return ex is TimeoutException ||
               ex is System.Net.Http.HttpRequestException ||
               (ex is Grpc.Core.RpcException rpcEx && rpcEx.StatusCode == Grpc.Core.StatusCode.Unavailable);
    }
}
