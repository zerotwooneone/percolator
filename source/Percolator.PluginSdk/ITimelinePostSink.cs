namespace Percolator.PluginSdk;

public interface ITimelinePostSink
{
    ValueTask PublishCardAsync(TimelineCardDto card, CancellationToken ct = default);
    ValueTask UpdateCardStatusAsync(Guid cardId, string newSummary, TimelineCardMetadata newMetadata, IReadOnlyList<TimelineActionDto> newActions, CancellationToken ct = default);
    ValueTask RemoveCardAsync(Guid cardId, CancellationToken ct = default);
}
