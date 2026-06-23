using Grpc.Core;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class RelayGroupService : Percolator.Contracts.RelayGroupService.RelayGroupServiceBase
{
    private readonly IRelayGroupOrchestrator _orchestrator;

    public RelayGroupService(IRelayGroupOrchestrator orchestrator)
        => _orchestrator = orchestrator;

    public override async Task<SubmitGroupMessageResponse> Publish(
        SubmitGroupMessageRequest request,
        ServerCallContext context)
    {
        try
        {
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
}
