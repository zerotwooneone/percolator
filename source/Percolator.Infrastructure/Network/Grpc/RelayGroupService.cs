using Grpc.Core;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class RelayGroupService : Percolator.Contracts.RelayGroupService.RelayGroupServiceBase
{
    private readonly IRelayGroupOrchestrator _orchestrator;
    private readonly IRelayGroupLedgerRepository _ledgerRepository;
    private readonly IRelayGroupStreamDispatcher _dispatcher;

    public RelayGroupService(
        IRelayGroupOrchestrator orchestrator,
        IRelayGroupLedgerRepository ledgerRepository,
        IRelayGroupStreamDispatcher dispatcher)
    {
        _orchestrator = orchestrator;
        _ledgerRepository = ledgerRepository;
        _dispatcher = dispatcher;
    }

    public override async Task<SubmitGroupMessageResponse> Publish(
        SubmitGroupMessageRequest request,
        ServerCallContext context)
    {
        try
        {
            // Validate required fields
            if (request.ConversationId is null)
                throw new ArgumentException("conversation_id is required.");
            if (request.Presentation is null)
                throw new ArgumentException("presentation is required.");
            if (request.Ciphertext is null)
                throw new ArgumentException("ciphertext is required.");

            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            // Rule: Use DomainType.FromBytesOwned(byteString.ToByteArray())
            // This prevents memory corruption by taking ownership of the defensive copy.
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());
            var ciphertext = CiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());

            await _orchestrator.PublishGroupRelayMessageAsync(
                conversationId,
                request.Epoch,
                presentation,
                ciphertext,
                context.CancellationToken);

            // Extract sender's PublicIdentityId from context (set by DeliveryCertificateAuthInterceptor)
            var senderPublicIdentityIdStr = context.RequestHeaders.GetValue("x-percolator-sender-public-identity-id");
            if (senderPublicIdentityIdStr is not null)
            {
                var senderPublicIdentityIdBytes = Convert.FromHexString(senderPublicIdentityIdStr);
                var senderPublicIdentityId = new Guid(senderPublicIdentityIdBytes);
                
                // Fan out to all connected streams
                await _dispatcher.DispatchAsync(
                    conversationId.Value,
                    request.Ciphertext.Memory,
                    request.Epoch,
                    senderPublicIdentityId,
                    context.CancellationToken);
            }

            return new SubmitGroupMessageResponse { Success = true };
        }
        catch (UnauthorizedDomainException ex)
        {
            // Map auth failure to Unauthenticated
            throw new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));
        }
        catch (EpochConflictDomainException ex)
        {
            // Map concurrency/epoch conflict to Aborted
            throw new RpcException(new Status(StatusCode.Aborted, ex.Message));
        }
        catch (ArgumentException ex)
        {
            // Map validation errors to InvalidArgument
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception)
        {
            // Generic Internal Server Error to prevent leaking sensitive domain details
            throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
        }
    }

    public override async Task<ProvisionGroupResponse> ProvisionGroup(
        ProvisionGroupRequest request,
        ServerCallContext context)
    {
        try
        {
            // Validate required fields
            if (request.ConversationId is null)
                throw new ArgumentException("conversation_id is required.");
            if (request.PublicParams is null)
                throw new ArgumentException("public_params is required.");
            if (request.MemberPublicIdentityIds.Count ==0)
                throw new ArgumentException("member_public_identity_ids must contain at least one member.");

            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(request.PublicParams.ToByteArray());

            // Convert PublicIdentityIds from protobuf to domain primitives
            var memberPublicIdentityIds = request.MemberPublicIdentityIds
                .Select(id => new Percolator.Chat.GroupLedger.PublicIdentityId(new Guid(id.ToByteArray())))
                .ToList();

            await _ledgerRepository.ProvisionNewGroupAsync(
                conversationId,
                publicParams,
                memberPublicIdentityIds,
                context.CancellationToken).ConfigureAwait(false);

            return new ProvisionGroupResponse { Success = true };
        }
        catch (ArgumentException ex)
        {
            // Map validation errors to InvalidArgument
            return new ProvisionGroupResponse { Success = false, Error = ex.Message };
        }
        catch (Exception)
        {
            // Generic Internal Server Error to prevent leaking sensitive domain details
            return new ProvisionGroupResponse { Success = false, Error = "Internal relay error." };
        }
    }

    public override async Task StreamGroupMessages(
        GroupStreamRequest request,
        IServerStreamWriter<GroupStreamResponse> responseStream,
        ServerCallContext context)
    {
        try
        {
            // Validate required fields
            if (request.ConversationId is null)
                throw new ArgumentException("conversation_id is required.");

            var conversationId = new Guid(request.ConversationId.ToByteArray());

            // Extract caller's PublicIdentityId from context (set by DeliveryCertificateAuthInterceptor)
            var senderPublicIdentityIdStr = context.RequestHeaders.GetValue("x-percolator-sender-public-identity-id");
            if (senderPublicIdentityIdStr is null)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing sender PublicIdentityId header"));

            var senderPublicIdentityIdBytes = Convert.FromHexString(senderPublicIdentityIdStr);
            var senderPublicIdentityId = new Guid(senderPublicIdentityIdBytes);

            // Authorization Gate: Verify caller is a member of the conversation
            var isMember = await _ledgerRepository.IsMemberAsync(
                new ConversationId(conversationId),
                new Percolator.Chat.GroupLedger.PublicIdentityId(senderPublicIdentityId),
                context.CancellationToken);

            if (!isMember)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller is not a member of this conversation"));

            // Register stream and get channel reader
            var channelReader = _dispatcher.RegisterStream(conversationId, senderPublicIdentityId);

            try
            {
                // Pump messages from channel to gRPC stream
                await foreach (var msg in channelReader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(msg, context.CancellationToken);
                }
            }
            finally
            {
                // Unregister stream on exit or cancellation
                _dispatcher.UnregisterStream(conversationId, senderPublicIdentityId);
            }
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (RpcException)
        {
            // Re-throw RPC exceptions as-is
            throw;
        }
        catch (Exception)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
        }
    }
}
