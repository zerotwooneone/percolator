using Percolator.SourceGenerators;

namespace Percolator.Chat.ValueObjects;

/// <summary>
/// Identifier for a group's avatar image.
/// </summary>
[ByteArray(minLength: 1, maxLength: 500)]
public sealed partial record GroupAvatarId;
