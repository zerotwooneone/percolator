using Grpc.Core;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class RelayGroupService : Percolator.Contracts.RelayGroupService.RelayGroupServiceBase
{
    private readonly IRelayGroupOrchestrator _orchestrator;
    private readonly IRelayGroupLedgerRepository _ledgerRepository;
    private readonly IRelayGroupStreamDispatcher _dispatcher;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly IPeerIdentityQueries _peerIdentityQueries;

    public RelayGroupService(
        IRelayGroupOrchestrator orchestrator,
        IRelayGroupLedgerRepository ledgerRepository,
        IRelayGroupStreamDispatcher dispatcher,
        IPeerIdentityRepository peerIdentityRepository,
        IPeerIdentityQueries peerIdentityQueries)
    {
        _orchestrator = orchestrator;
        _ledgerRepository = ledgerRepository;
        _dispatcher = dispatcher;
        _peerIdentityRepository = peerIdentityRepository;
        _peerIdentityQueries = peerIdentityQueries;
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
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());
            var ciphertext = CiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());

            var status = await _orchestrator.PublishGroupRelayMessageAsync(
                conversationId,
                request.Epoch,
                presentation,
                ciphertext,
                context.CancellationToken);

            // Map status to gRPC response
            if (status == RelayGroupOperationStatus.Success)
            {
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
            else if (status == RelayGroupOperationStatus.EpochConflict)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "Epoch conflict: client state is stale."));
            }
            else if (status == RelayGroupOperationStatus.Unauthorized)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed."));
            }
            else if (status == RelayGroupOperationStatus.GroupNotFound)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
            }
            else
            {
                throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
            }
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception)
        {
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
            if (request.MemberPublicIdentityIds.Count == 0)
                throw new ArgumentException("member_public_identity_ids must contain at least one member.");
            if (request.EncryptedProfile is null)
                throw new ArgumentException("encrypted_profile is required.");

            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(request.PublicParams.ToByteArray());
            var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(request.EncryptedProfile.ToByteArray());

            // Convert PublicIdentityIds from protobuf to domain primitives, then map to PeerIds
            var memberPeerIds = new List<Percolator.Chat.GroupMembership.ChatPeerId>();
            foreach (var idBytes in request.MemberPublicIdentityIds)
            {
                var publicIdentityId = new Percolator.Identity.PublicIdentityId(new Guid(idBytes.ToByteArray()));
                var peerIdentity = await _peerIdentityRepository.GetOrCreateAsync(publicIdentityId, context.CancellationToken);
                memberPeerIds.Add(new Percolator.Chat.GroupMembership.ChatPeerId(peerIdentity.Id.Value));
            }

            await _ledgerRepository.ProvisionNewGroupAsync(
                conversationId,
                publicParams,
                encryptedProfile,
                memberPeerIds,
                context.CancellationToken).ConfigureAwait(false);

            return new ProvisionGroupResponse { Success = true };
        }
        catch (ArgumentException ex)
        {
            return new ProvisionGroupResponse { Success = false, Error = ex.Message };
        }
        catch (Exception)
        {
            return new ProvisionGroupResponse { Success = false, Error = "Internal relay error." };
        }
    }

    public override async Task<ModifyGroupResponse> ModifyGroup(
        ModifyGroupRequest request,
        ServerCallContext context)
    {
        try
        {
            // Validate required fields
            if (request.ConversationId is null)
                throw new ArgumentException("conversation_id is required.");
            if (request.Presentation is null)
                throw new ArgumentException("presentation is required.");
            if (request.NewEncryptedProfile is null)
                throw new ArgumentException("new_encrypted_profile is required.");

            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());
            var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(request.NewEncryptedProfile.ToByteArray());

            // Convert PublicIdentityIds from protobuf to domain primitives
            var addPublicIdentityIds = request.AddPublicIdentityIds
                .Select(id => new Percolator.Identity.PublicIdentityId(new Guid(id.ToByteArray())))
                .ToList();

            var removePublicIdentityIds = request.RemovePublicIdentityIds
                .Select(id => new Percolator.Identity.PublicIdentityId(new Guid(id.ToByteArray())))
                .ToList();

            var status = await _orchestrator.ModifyGroupAsync(
                conversationId,
                request.BaseEpoch,
                presentation,
                newEncryptedProfile,
                addPublicIdentityIds,
                removePublicIdentityIds,
                context.CancellationToken);

            // Map status to gRPC response
            if (status == RelayGroupOperationStatus.Success)
            {
                return new ModifyGroupResponse { Success = true };
            }
            else if (status == RelayGroupOperationStatus.EpochConflict)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "Epoch conflict: client state is stale."));
            }
            else if (status == RelayGroupOperationStatus.Unauthorized)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed."));
            }
            else if (status == RelayGroupOperationStatus.GroupNotFound)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
            }
            else
            {
                throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
            }
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
        }
    }

    public override async Task<GetGroupStateResponse> GetGroupState(
        GetGroupStateRequest request,
        ServerCallContext context)
    {
        try
        {
            // Validate required fields
            if (request.ConversationId is null)
                throw new ArgumentException("conversation_id is required.");
            if (request.Presentation is null)
                throw new ArgumentException("presentation is required.");

            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());

            var (status, ledger) = await _orchestrator.GetGroupStateAsync(
                conversationId,
                presentation,
                context.CancellationToken);

            // Map status to gRPC response
            if (status == RelayGroupOperationStatus.Success && ledger is not null)
            {
                return new GetGroupStateResponse
                {
                    CurrentEpoch = ledger.CurrentEpoch,
                    PublicParams = Google.Protobuf.ByteString.CopyFrom(ledger.GroupPublicParams.Span),
                    EncryptedProfile = Google.Protobuf.ByteString.CopyFrom(ledger.EncryptedProfile.Span)
                };
            }
            else if (status == RelayGroupOperationStatus.Unauthorized)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed."));
            }
            else if (status == RelayGroupOperationStatus.GroupNotFound)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
            }
            else
            {
                throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
            }
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
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

            // Map PublicIdentityId to PeerId for authorization check
            var senderPeerId = await _peerIdentityQueries.GetPeerIdByPublicIdentityIdAsync(
                new Percolator.Identity.PublicIdentityId(senderPublicIdentityId),
                context.CancellationToken);

            if (senderPeerId is null)
                throw new RpcException(new Status(StatusCode.NotFound, "Sender peer identity not found"));

            // Authorization Gate: Verify caller is a member of the conversation
            var isMember = await _ledgerRepository.IsMemberAsync(
                new ConversationId(conversationId),
                new Percolator.Chat.GroupMembership.ChatPeerId(senderPeerId.Value.Value),
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
