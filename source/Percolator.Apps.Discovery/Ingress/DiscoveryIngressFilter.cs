using System.Collections.Concurrent;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Ingress;

/// <summary>
/// Fast, zero-allocation hot-path ingress filter for Discovery payloads (AppId = 0x02).
/// Enforces relay policy gates (Disabled / BlindedContactsOnly) and per-sender rate limits
/// before payload deserialization.
/// </summary>
public sealed class DiscoveryIngressFilter : IAppIngressFilter
{
    public const int DefaultMaxRequestsPerWindow = 30;
    public static readonly TimeSpan DefaultRateLimitWindow = TimeSpan.FromMinutes(1);

    private sealed class SenderWindowState
    {
        public DateTimeOffset WindowStartUtc;
        public int Count;
    }

    private readonly IRelayPeerDirectory _directory;
    private readonly IDateTimeProvider? _clock;
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _rateLimitWindow;
    private readonly ConcurrentDictionary<PublicIdentityId, SenderWindowState> _rateLimits = new();

    public AppId TargetAppId => AppId.Discovery;
    public bool IsEnabled { get; set; } = true;

    public DiscoveryIngressFilter(
        IRelayPeerDirectory directory,
        IDateTimeProvider? clock = null,
        int maxRequestsPerWindow = DefaultMaxRequestsPerWindow,
        TimeSpan? rateLimitWindow = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _clock = clock;
        _maxRequestsPerWindow = maxRequestsPerWindow > 0 ? maxRequestsPerWindow : DefaultMaxRequestsPerWindow;
        _rateLimitWindow = rateLimitWindow ?? DefaultRateLimitWindow;
    }

    public IngressDecision Evaluate(InboundPayloadContext context)
    {
        if (!IsEnabled || context.AppId != TargetAppId || context.Payload.IsEmpty)
        {
            return IngressDecision.DiscardSilently;
        }

        RelayDiscoveryPolicy policy = _directory.Policy;
        if (policy == RelayDiscoveryPolicy.Disabled)
        {
            return IngressDecision.DiscardSilently;
        }

        DiscoveryOpcode opcode = (DiscoveryOpcode)context.Payload.Span[0];

        // When relay is configured for BlindedContactsOnly, drop open NearbyQuery frames immediately
        if (policy == RelayDiscoveryPolicy.BlindedContactsOnly && opcode == DiscoveryOpcode.NearbyQuery)
        {
            return IngressDecision.DiscardSilently;
        }

        if (!CheckRateLimit(context.SenderIdentityId, context.ReceivedAtUtc))
        {
            return IngressDecision.DiscardSilently;
        }

        return IngressDecision.Accept;
    }

    public ValueTask<IngressDecision> EvaluateAsync(InboundPayloadContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(Evaluate(context));

    private bool CheckRateLimit(PublicIdentityId senderId, DateTimeOffset contextTimestampUtc)
    {
        if (!senderId.IsValid)
        {
            return false;
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? contextTimestampUtc;
        var state = _rateLimits.GetOrAdd(senderId, _ => new SenderWindowState
        {
            WindowStartUtc = nowUtc,
            Count = 0
        });

        lock (state)
        {
            if (nowUtc - state.WindowStartUtc >= _rateLimitWindow)
            {
                state.WindowStartUtc = nowUtc;
                state.Count = 1;
                return true;
            }

            if (state.Count >= _maxRequestsPerWindow)
            {
                return false;
            }

            state.Count++;
            return true;
        }
    }
}
