namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Protos;

public sealed class ExchangeTransferPipeEndpoint : TransferPipeService.TransferPipeServiceBase
{
    private readonly InMemoryTransferPipeBroker _broker;
    private readonly IRelayHostingPolicyProvider _policyProvider;

    public ExchangeTransferPipeEndpoint(
        InMemoryTransferPipeBroker broker,
        IRelayHostingPolicyProvider policyProvider)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
    }

    public override async Task OpenPipe(
        IAsyncStreamReader<PipeChunkProto> requestStream,
        IServerStreamWriter<PipeChunkProto> responseStream,
        ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.TransferPipe))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "TransferPipe feature is disabled on this relay."));
        }

        // First frame must provide the 32-byte random one-time PipeRendezvousToken
        bool hasInitialFrame;
        using (var firstFrameCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
        {
            firstFrameCts.CancelAfter(policy.PipeIdleTimeout);
            try
            {
                hasInitialFrame = await requestStream.MoveNext(firstFrameCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Timed out waiting for initial pipe rendezvous frame."));
            }
        }

        if (!hasInitialFrame)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Initial pipe rendezvous frame is required."));
        }

        var firstFrame = requestStream.Current;
        if (firstFrame.RendezvousToken.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "PipeRendezvousToken must be exactly 32 bytes."));
        }

        if (firstFrame.Payload.Length > InMemoryTransferPipeBroker.MaxFramePayloadBytes)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Pipe frame payload exceeds maximum {InMemoryTransferPipeBroker.MaxFramePayloadBytes} bytes."));
        }

        var joinResult = _broker.JoinOrCreate(firstFrame.RendezvousToken.Span, policy);
        if (joinResult.IsFailure)
        {
            var code = joinResult.Error.Code == "PIPE_CONCURRENCY_EXCEEDED"
                ? StatusCode.ResourceExhausted
                : StatusCode.InvalidArgument;
            throw new RpcException(new Status(code, joinResult.Error.Description));
        }

        using var handle = joinResult.Value;
        var slot = handle.Slot;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            slot.SessionCts.Token);

        // Wait for partner caller to join with the matching token
        if (handle.IsInitiator)
        {
            try
            {
                await slot.PeerJoinedTcs.Task.WaitAsync(slot.IdleTimeout, linkedCts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                slot.Abort("PIPE_IDLE_TIMEOUT");
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Transfer pipe peer did not join before idle timeout."));
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "Transfer pipe cancelled while waiting for peer."));
            }
        }

        // Forward initial payload if present on the rendezvous frame
        if (!firstFrame.Payload.IsEmpty)
        {
            await EnqueueValidatedPayloadAsync(handle, firstFrame.Payload.ToByteArray(), linkedCts.Token).ConfigureAwait(false);
        }

        var readTask = PumpInboundAsync(requestStream, handle, linkedCts.Token);
        var writeTask = PumpOutboundAsync(responseStream, handle, linkedCts.Token);

        await Task.WhenAll(readTask, writeTask).ConfigureAwait(false);

        if (slot.AbortReason == "PIPE_SESSION_QUOTA_EXCEEDED")
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Transfer pipe per-session byte quota exceeded."));
        }

        if (slot.AbortReason == "PIPE_IDLE_TIMEOUT")
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Transfer pipe closed due to idle timeout."));
        }
    }

    private static async Task EnqueueValidatedPayloadAsync(
        PipeParticipantHandle handle,
        byte[] payload,
        CancellationToken ct)
    {
        if (payload.Length > InMemoryTransferPipeBroker.MaxFramePayloadBytes)
        {
            handle.Slot.Abort("PIPE_FRAME_TOO_LARGE");
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Pipe frame payload exceeds maximum {InMemoryTransferPipeBroker.MaxFramePayloadBytes} bytes."));
        }

        if (!handle.Slot.TryReserveSessionBytes(payload.Length))
        {
            handle.Slot.Abort("PIPE_SESSION_QUOTA_EXCEEDED");
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Transfer pipe per-session byte quota exceeded."));
        }

        await handle.Slot.ThrottleBandwidthAsync(payload.Length, ct).ConfigureAwait(false);
        await handle.OutgoingWriter.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private static async Task PumpInboundAsync(
        IAsyncStreamReader<PipeChunkProto> requestStream,
        PipeParticipantHandle handle,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(handle.Slot.IdleTimeout);

                bool moved;
                try
                {
                    moved = await requestStream.MoveNext(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    handle.Slot.Abort("PIPE_IDLE_TIMEOUT");
                    return;
                }

                if (!moved)
                {
                    break;
                }

                var frame = requestStream.Current;
                if (frame.Payload.IsEmpty)
                {
                    continue;
                }

                await EnqueueValidatedPayloadAsync(handle, frame.Payload.ToByteArray(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            handle.OutgoingWriter.TryComplete();
        }
    }

    private static async Task PumpOutboundAsync(
        IServerStreamWriter<PipeChunkProto> responseStream,
        PipeParticipantHandle handle,
        CancellationToken ct)
    {
        try
        {
            await foreach (var payload in handle.IncomingReader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new PipeChunkProto
                {
                    Payload = ByteString.CopyFrom(payload)
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
