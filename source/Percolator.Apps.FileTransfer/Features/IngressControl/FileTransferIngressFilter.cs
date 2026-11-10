using Percolator.Apps.FileTransfer.Ports;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public sealed class FileTransferIngressFilter : IAppIngressFilter
{
    private readonly IFileTransferFilterPolicyRepository _policyRepository;
    private volatile bool _isEnabled = false;

    public const string PolicyDisabledErrorCode = "FILE_TRANSFER_DISABLED_BY_PRIVACY_POLICY";
    public const string PrivacyNotice = "Direct P2P file transfer uses multi-stream TCP connections that expose your IP address to the peer and is significantly less private than relayed chat.";

    public AppId TargetAppId => AppId.FileTransferControl;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public FileTransferIngressFilter(IFileTransferFilterPolicyRepository policyRepository)
    {
        _policyRepository = policyRepository ?? throw new ArgumentNullException(nameof(policyRepository));
    }

    public async ValueTask<IngressDecision> EvaluateAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        if (!_isEnabled)
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
        if (!_isEnabled)
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
