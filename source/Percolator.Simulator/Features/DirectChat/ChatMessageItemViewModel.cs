using System;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class ChatMessageItemViewModel
{
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string TimestampText { get; init; } = string.Empty;
    public bool IsOutgoing { get; init; }
    public string StatusText { get; init; } = string.Empty;

    public string DisplayText => $"[{TimestampText}] {(IsOutgoing ? "You" : SenderName)}: {Content}";

    public override string ToString() => DisplayText;
}
