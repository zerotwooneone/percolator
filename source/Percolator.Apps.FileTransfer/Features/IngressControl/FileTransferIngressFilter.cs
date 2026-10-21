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
        _policyRepository = policyRepository ?? throw new ArgumentNullException(nameof(policyRepository));
    }

    public async ValueTask<IngressDecision> EvaluateAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return IngressDecision.DiscardSilently;

        FileTransferFilterPolicy policy = await _policyRepository.GetPolicyAsync(context.SenderIdentityId, ct);
        if (policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked)
        {
            return IngressDecision.DiscardSilently;
        }

        return IngressDecision.Accept;
    }

    public IngressDecision Evaluate(InboundPayloadContext context)
    {
        if (!IsEnabled)
            return IngressDecision.DiscardSilently;

        ValueTask<FileTransferFilterPolicy> policyTask = _policyRepository.GetPolicyAsync(context.SenderIdentityId);
        FileTransferFilterPolicy policy = policyTask.IsCompletedSuccessfully
            ? policyTask.Result
            : policyTask.AsTask().GetAwaiter().GetResult();

        return policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked
            ? IngressDecision.DiscardSilently
            : IngressDecision.Accept;
    }
}
