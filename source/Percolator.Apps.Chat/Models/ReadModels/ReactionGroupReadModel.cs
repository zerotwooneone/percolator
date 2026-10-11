using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models.ReadModels;

public sealed record ReactionGroupReadModel(
    string Emoji,
    int Count,
    IReadOnlyList<PublicIdentityId> Reactors,
    bool HasReactedByCurrentUser);
