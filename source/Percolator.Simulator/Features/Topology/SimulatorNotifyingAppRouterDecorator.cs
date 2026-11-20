using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ingress;
using Percolator.Domain.Common;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatorNotifyingAppRouterDecorator : IAppRouter
{
    private readonly IAppRouter _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;

    public SimulatorNotifyingAppRouterDecorator(
        IAppRouter inner,
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    public void RegisterHandler(IAppPayloadHandler handler)
    {
        _inner.RegisterHandler(handler);
    }

    public DomainResult<IAppPayloadHandler> Resolve(AppId appId)
    {
        var result = _inner.Resolve(appId);
        if (!result.IsSuccess)
        {
            return result;
        }

        var decoratedHandler = new SimulatorNotifyingPayloadHandlerDecorator(result.Value!, _stateBus, _nodeId);
        return DomainResult<IAppPayloadHandler>.Success(decoratedHandler);
    }

    public void RegisterFilter(IAppIngressFilter filter)
    {
        _inner.RegisterFilter(filter);
    }

    public IAppIngressFilter? ResolveFilter(AppId appId)
    {
        return _inner.ResolveFilter(appId);
    }
}

internal sealed class SimulatorNotifyingPayloadHandlerDecorator : IAppPayloadHandler
{
    private readonly IAppPayloadHandler _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;

    public AppId TargetAppId => _inner.TargetAppId;

    public SimulatorNotifyingPayloadHandlerDecorator(
        IAppPayloadHandler inner,
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    public async ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        var result = await _inner.HandleInboundAsync(context, ct);
        if (result.IsSuccess)
        {
            if (TargetAppId == AppId.FileTransferControl)
            {
                byte opcode = context.Payload.Length > 0 ? context.Payload.Span[0] : (byte)0;
                var category = opcode == 1
                    ? SignalCategory.FileTransferOfferReceived
                    : SignalCategory.SwarmProgressChanged;

                _stateBus.Publish(new SimulatorStateSignal(
                    _nodeId,
                    category,
                    Details: context.SenderIdentityId.ToString(),
                    TargetChannelId: context.ChannelId));
            }
            else
            {
                var category = TargetAppId == AppId.Chat
                    ? SignalCategory.ChatTranscriptChanged
                    : SignalCategory.GroupStateChanged;

                _stateBus.Publish(new SimulatorStateSignal(
                    _nodeId,
                    category,
                    Details: context.SenderIdentityId.ToString(),
                    Payload: new ChatTranscriptSignalPayload(context.SenderIdentityId),
                    TargetChannelId: context.ChannelId));

                _stateBus.Publish(new SimulatorStateSignal(
                    _nodeId,
                    SignalCategory.PendingHandshakeChanged,
                    Details: context.SenderIdentityId.ToString()));
            }
        }

        return result;
    }
}
