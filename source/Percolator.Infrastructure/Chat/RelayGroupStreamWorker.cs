using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Infrastructure.Network;
using RelayGroupService = Percolator.Contracts.RelayGroupService;

namespace Percolator.Infrastructure.Chat;

public sealed class RelayGroupStreamWorker : IHostedService
{
    private readonly IGroupStreamIngressProcessor _ingressProcessor;
    private readonly IGroupConversationQueries _groupConversationQueries;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly ILogger<RelayGroupStreamWorker> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<Percolator.Chat.Messaging.ValueObjects.ConversationId, Task> _activeStreams = new();

    public RelayGroupStreamWorker(
        IGroupStreamIngressProcessor ingressProcessor,
        IGroupConversationQueries groupConversationQueries,
        IServiceProvider serviceProvider,
        IPeerGrpcChannelFactory channelFactory,
        IPeerRoutingProfileRepository peerRoutingProfileRepository,
        ISelfIdentityQueries selfIdentityQueries,
        ILogger<RelayGroupStreamWorker> logger)
    {
        _ingressProcessor = ingressProcessor;
        _groupConversationQueries = groupConversationQueries;
        _serviceProvider = serviceProvider;
        _channelFactory = channelFactory;
        _peerRoutingProfileRepository = peerRoutingProfileRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RelayGroupStreamWorker starting");

        // Start the background stream management loop
        _ = Task.Run(() => ManageStreamsAsync(_shutdownCts.Token), _shutdownCts.Token);
    }

    private async Task ManageStreamsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Query for all active group conversations
                var activeConversations = await _groupConversationQueries.ListActiveAsync(cancellationToken);

                // Ensure streams are open for all active conversations
                foreach (var conversation in activeConversations)
                {
                    if (!_activeStreams.ContainsKey(conversation.ConversationId))
                    {
                        var streamTask = StartStreamForConversationAsync(conversation, cancellationToken);
                        _activeStreams[conversation.ConversationId] = streamTask;
                    }
                }

                // Clean up streams for conversations that are no longer active
                var activeConversationIds = activeConversations.Select(c => c.ConversationId).ToHashSet();
                var streamsToRemove = _activeStreams.Keys.Where(id => !activeConversationIds.Contains(id)).ToList();
                foreach (var conversationId in streamsToRemove)
                {
                    _activeStreams.Remove(conversationId, out _);
                    _logger.LogInformation("Removed stream for conversation {ConversationId}", conversationId);
                }

                // Wait before next check
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in stream management loop");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task StartStreamForConversationAsync(GroupConversationDto conversation, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting stream for conversation {ConversationId}", conversation.ConversationId);

        try
        {
            // Resolve self identity for this conversation
            var identitySelfId = new Percolator.Identity.SelfId(conversation.SelfIdentityId.Value);
            var selfIdentity = await _selfIdentityQueries.GetSelfIdentityCryptoInfoAsync(identitySelfId, cancellationToken);
            if (selfIdentity is null)
            {
                _logger.LogWarning("Self identity not found for {SelfIdentityId}", conversation.SelfIdentityId);
                return;
            }

            // Get relay endpoint from routing profile
            var relayPeerId = new NetworkPeerId(conversation.RelayPeerId.Value);
            var relayProfile = await _peerRoutingProfileRepository.GetByIdAsync(relayPeerId, cancellationToken);
            
            if (relayProfile is null)
            {
                _logger.LogWarning("No routing profile found for relay {RelayPeerId}", conversation.RelayPeerId);
                return;
            }

            var grpcEndpoint = relayProfile.Endpoints.FirstOrDefault();
            if (grpcEndpoint is null)
            {
                _logger.LogWarning("No gRPC endpoint found for relay {RelayPeerId}", conversation.RelayPeerId);
                return;
            }

            // Create gRPC channel and client
            using var channel = _channelFactory.CreateChannel(grpcEndpoint.EndPoint);
            var client = new RelayGroupService.RelayGroupServiceClient(channel);

            // Create stream request
            var request = new GroupStreamRequest
            {
                ConversationId = Google.Protobuf.ByteString.CopyFrom(conversation.ConversationId.Value.ToByteArray())
            };

            // Add authorization header with public identity ID
            var headers = new Grpc.Core.Metadata
            {
                { "x-percolator-sender-public-identity-id", selfIdentity.Value.PublicIdentityId.Value.ToString("N") }
            };

            // Start streaming
            var callOptions = new Grpc.Core.CallOptions(headers: headers, cancellationToken: cancellationToken);
            using var call = client.StreamGroupMessages(request, callOptions);

            // Process incoming messages
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                var conversationIdBytes = new Guid(response.ConversationId.ToByteArray());
                var senderPublicIdentityIdBytes = new Guid(response.SenderPublicIdentityId.ToByteArray());
                var ciphertext = response.Ciphertext.ToByteArray();
                var epoch = response.Epoch;
                var senderDeviceId = response.SenderDeviceId;

                await _ingressProcessor.ProcessGroupMessageAsync(
                    conversationIdBytes,
                    senderPublicIdentityIdBytes,
                    epoch,
                    ciphertext,
                    conversation.SelfIdentityId,
                    senderDeviceId,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stream for conversation {ConversationId} cancelled", conversation.ConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in stream for conversation {ConversationId}", conversation.ConversationId);
        }
        finally
        {
            _activeStreams.Remove(conversation.ConversationId, out _);
            _logger.LogInformation("Stream for conversation {ConversationId} ended", conversation.ConversationId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RelayGroupStreamWorker stopping");
        _shutdownCts.Cancel();
        
        // Wait for all active streams to complete
        return Task.WhenAll(_activeStreams.Values);
    }
}
