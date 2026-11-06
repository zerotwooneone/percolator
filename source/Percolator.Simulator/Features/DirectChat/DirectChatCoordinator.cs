using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class DirectChatCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public DirectChatCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public Task<bool> SendDirectMessageAsync(
        SimulatedNodeId senderNodeId,
        SimulatedNodeId recipientNodeId,
        string text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);

        return Task.Run(async () =>
        {
            var sender = _worldState.GetPersonaHost(senderNodeId);
            var recipient = _worldState.GetPersonaHost(recipientNodeId);

            if (sender == null || recipient == null)
            {
                _toastService.ShowToast("Send Failed", "Both sender and recipient must be active.", ToastSeverity.Error);
                return false;
            }

            try
            {
                var channelId = ChannelIdUtility.DeterministicFor(sender.PublicIdentityId, recipient.PublicIdentityId);

                // Ensure channel exists in repositories
                await EnsureChannelAsync(sender, recipient, channelId, ct);

                using var scope = sender.Services.CreateScope();
                var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

                var command = new SendMessageCommand(
                    ChannelId: channelId,
                    AuthorId: sender.PublicIdentityId,
                    RecipientIdentityId: recipient.PublicIdentityId,
                    RecipientDeviceId: DeviceId.Primary,
                    Content: text,
                    Mentions: null,
                    ReplyContext: null,
                    Attachments: null,
                    ExpireAfterSeconds: null,
                    LinkPreview: null,
                    Route: Percolator.PluginSdk.DeliveryRoute.DirectP2P);

                var result = await chatService.SendMessageAsync(command, ct);
                if (!result.IsSuccess)
                {
                    _toastService.ShowToast("Send Error", result.Error.Description, ToastSeverity.Error);
                    return false;
                }

                _stateBus.Publish(new SimulatorStateSignal(
                    sender.Id,
                    SignalCategory.ChatTranscriptChanged,
                    Details: recipient.PublicIdentityId.ToString()));

                // Immediately flush outbox to deliver encrypted payload via loopback gRPC TLS
                var flushResult = await sender.FlushOutboxAsync(ct);
                if (!flushResult.IsSuccess)
                {
                    _toastService.ShowToast("Delivery Deferred", flushResult.Error.Description, ToastSeverity.Warning);
                    return false;
                }

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    sender.DisplayName,
                    sender.Id,
                    "DirectChat",
                    $"Direct message sent to '{recipient.DisplayName}': \"{text}\""));

                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    sender.DisplayName,
                    sender.Id,
                    "DirectChat",
                    $"Failed to send message: {ex.Message}",
                    ex.ToString()));

                _toastService.ShowToast("Send Exception", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }

    public Task<IReadOnlyList<ChatMessageItemViewModel>> GetTranscriptAsync(
        SimulatedNodeId nodeId,
        SimulatedNodeId? peerNodeId = null,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<ChatMessageItemViewModel>>(async () =>
        {
            var host = _worldState.GetPersonaHost(nodeId);
            if (host == null) return Array.Empty<ChatMessageItemViewModel>();

            try
            {
                using var scope = host.Services.CreateScope();
                var chatRepo = scope.ServiceProvider.GetRequiredService<IChatMessageRepository>();

                if (peerNodeId.HasValue)
                {
                    var peerHost = _worldState.GetPersonaHost(peerNodeId.Value);
                    if (peerHost != null)
                    {
                        var channelId = ChannelIdUtility.DeterministicFor(host.PublicIdentityId, peerHost.PublicIdentityId);
                        var messages = await chatRepo.GetByChannelIdAsync(channelId, ct);

                        return messages.Select(m =>
                        {
                            var isOutgoing = m.AuthorId == host.PublicIdentityId;
                            var senderName = isOutgoing ? host.DisplayName : peerHost.DisplayName;
                            var timeText = DateTimeOffset.FromUnixTimeMilliseconds((long)m.TimestampUnixMs).ToLocalTime().ToString("HH:mm:ss");

                            return new ChatMessageItemViewModel
                            {
                                SenderName = senderName,
                                Content = m.Content,
                                TimestampText = timeText,
                                IsOutgoing = isOutgoing,
                                StatusText = "✓"
                            };
                        }).ToList();
                    }
                }

                return Array.Empty<ChatMessageItemViewModel>();
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    host.DisplayName,
                    host.Id,
                    "DirectChat",
                    $"Error fetching chat transcript: {ex.Message}"));

                return Array.Empty<ChatMessageItemViewModel>();
            }
        }, ct);
    }

    private static async Task EnsureChannelAsync(
        ISimulatedPersonaHost sender,
        ISimulatedPersonaHost recipient,
        Percolator.Domain.Channels.ValueObjects.ChannelId channelId,
        CancellationToken ct)
    {
        using (var scope = sender.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var time = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            if (await repo.GetDirectByIdAsync(channelId, ct) == null)
            {
                var ch = DirectChannel.Create(channelId, sender.PublicIdentityId, recipient.PublicIdentityId, time).Value!;
                await repo.SaveDirectAsync(ch, ct);
            }
        }

        using (var scope = recipient.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var time = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            if (await repo.GetDirectByIdAsync(channelId, ct) == null)
            {
                var ch = DirectChannel.Create(channelId, recipient.PublicIdentityId, sender.PublicIdentityId, time).Value!;
                await repo.SaveDirectAsync(ch, ct);
            }
        }
    }
}
