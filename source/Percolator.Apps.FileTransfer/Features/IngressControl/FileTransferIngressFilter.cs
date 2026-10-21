using Percolator.Apps.FileTransfer.Ports;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public sealed class FileTransferIngressFilter : IAppIngressFilter
{
    private readonly IFileTransferFilterPolicyRepository _policyRepository;

    public AppId TargetAppId => AppId.FileTransferControl;
    public bool IsEnabled { get; set; } = true;

    public FileTransferIngressFilter(IFileTransferFilterPolicyRepository policyRepository)
    {
        _policyRepository = policyRepository;
    }

    public IngressDecision Evaluate(InboundPayloadContext context)
    {
        if (!IsEnabled)
            return IngressDecision.DiscardSilently;

        FileTransferFilterPolicy policy = _policyRepository.GetPolicyAsync(context.SenderIdentityId).AsTask().GetAwaiter().GetResult();
        if (policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked)
        {
            return IngressDecision.DiscardSilently;
        }

        return IngressDecision.Accept;
    }
}
