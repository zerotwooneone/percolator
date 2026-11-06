using System;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed record ChatMessageItemViewModel
{
    public string MessageId { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string TimestampText { get; init; } = string.Empty;
    public bool IsOutgoing { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool IsEdited { get; init; }
    public bool IsRead { get; init; }
    public string ReactionsSummary { get; init; } = string.Empty;

    public string DisplayText
    {
        get
        {
            var editedMarker = IsEdited ? " (edited)" : string.Empty;
            var reactions = string.IsNullOrEmpty(ReactionsSummary) ? string.Empty : $" [{ReactionsSummary}]";
            return $"[{TimestampText}] {(IsOutgoing ? "You" : SenderName)}: {Content}{editedMarker} {StatusText}{reactions}".Trim();
        }
    }

    public override string ToString() => DisplayText;
}
