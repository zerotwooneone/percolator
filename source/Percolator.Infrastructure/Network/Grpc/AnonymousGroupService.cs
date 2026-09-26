using Grpc.Core;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.GroupLedger;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class AnonymousGroupService : Contracts.AnonymousGroupService.AnonymousGroupServiceBase
{
    private readonly IRelayGroupOrchestrator _orchestrator;

    public AnonymousGroupService(IRelayGroupOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public override async Task<ProcessAnonymousGroupResponse> ProcessAnonymousGroupRequest(
        AnonymousGroupRequest request,
        ServerCallContext context)
    {
        // Fail-fast Validation
        if (request.ConversationId is null || request.ConversationId.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "conversation_id is required"));

        if (request.PresentationProof is null || request.PresentationProof.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "presentation_proof is required"));

        if (request.TargetPublicIdentityIds.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_public_identity_ids must contain at least one identity"));

        try
        {
            // Map to Domain
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.PresentationProof.ToByteArray());

            var targetIdentities = request.TargetPublicIdentityIds
                .Select(id => new Percolator.Identity.PublicIdentityId(new Guid(id.ToByteArray())))
                .ToList();

            CiphertextBytes? ciphertext = null;
            if (request.Ciphertext is not null && request.Ciphertext.Length > 0)
            {
                ciphertext = CiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());
            }

            EncryptedGroupProfileBytes? newEncryptedEntries = null;
            if (request.NewEncryptedEntriesBlob is not null && request.NewEncryptedEntriesBlob.Length > 0)
            {
                newEncryptedEntries = EncryptedGroupProfileBytes.FromBytesOwned(request.NewEncryptedEntriesBlob.ToByteArray());
            }

            var status = await _orchestrator.ProcessAnonymousGroupRequestAsync(
                conversationId,
                request.SenderKeyId,
                presentation,
                targetIdentities,
                ciphertext,
                newEncryptedEntries,
                request.NewEpoch,
                context.CancellationToken);

            // Map statuses to RpcExceptions
            if (status == RelayGroupOperationStatus.Success)
            {
                return new ProcessAnonymousGroupResponse { Success = true };
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
        // Fail-fast on missing conversation_id
        if (request.ConversationId is null || request.ConversationId.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "conversation_id is required"));

        if (request.PresentationProof is null || request.PresentationProof.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "presentation_proof is required"));

        try
        {
            // Map to Domain
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.PresentationProof.ToByteArray());

            var (status, state) = await _orchestrator.GetGroupStateAsync(
                conversationId,
                presentation,
                context.CancellationToken);

            if (status == RelayGroupOperationStatus.Success && state is not null)
            {
                return new GetGroupStateResponse
                {
                    CurrentEpoch = state.Epoch.Value,
                    EncryptedEntriesBlob = Google.Protobuf.ByteString.CopyFrom(state.EncryptedEntriesBlob.Span)
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

            throw new RpcException(new Status(StatusCode.Internal, $"Unexpected status: {status}"));
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
}
