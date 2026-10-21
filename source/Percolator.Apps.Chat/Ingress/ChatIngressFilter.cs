using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Ingress;

/// <summary>
/// Fast pre-deserialization hot-path ingress filter for Chat payloads (AppId = 0x01).
/// Enforces the 64 KB Double Ratchet frame ceiling and per-sender sliding-window rate limits.
/// </summary>
public sealed class ChatIngressFilter : IAppIngressFilter
{
    public const int MaxWireFrameBytes = 64 * 1024; // 64 KB Double Ratchet ceiling
    public const int DefaultMaxFramesPerWindow = 120;
    public static readonly TimeSpan DefaultRateLimitWindow = TimeSpan.FromMinutes(1);

    private sealed class SenderWindowState
    {
        public DateTimeOffset WindowStartUtc;
        public int Count;
    }

    private readonly IDateTimeProvider? _clock;
    private readonly int _maxFramesPerWindow;
    private readonly TimeSpan _rateLimitWindow;
    private readonly ConcurrentDictionary<PublicIdentityId, SenderWindowState> _rateLimits = new();

    public AppId TargetAppId => AppId.Chat;
    public bool IsEnabled { get; set; } = true;

    public ChatIngressFilter(
        IDateTimeProvider? clock = null,
        int maxFramesPerWindow = DefaultMaxFramesPerWindow,
        TimeSpan? rateLimitWindow = null)
    {
        _clock = clock;
        _maxFramesPerWindow = maxFramesPerWindow > 0 ? maxFramesPerWindow : DefaultMaxFramesPerWindow;
        _rateLimitWindow = rateLimitWindow ?? DefaultRateLimitWindow;
    }

    public IngressDecision Evaluate(InboundPayloadContext context)
    {
        if (!IsEnabled || context.AppId != TargetAppId)
        {
            return IngressDecision.DiscardSilently;
        }

        if (context.Payload.IsEmpty || context.Payload.Length > MaxWireFrameBytes)
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

            if (state.Count >= _maxFramesPerWindow)
            {
                return false;
            }

            state.Count++;
            return true;
        }
    }
}
