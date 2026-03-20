namespace Desktop.Wpf.Shared.Models;

public sealed record StoreListChange<T>(
    StoreListChangeKind Kind,
    IReadOnlyList<T> Items,
    int? Index = null);