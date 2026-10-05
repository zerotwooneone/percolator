namespace Percolator.PluginSdk;

public sealed record TimelineActionDto(
    string ActionId,
    string Label,
    string Style,
    bool IsEnabled = true,
    string? ConfirmPrompt = null);
