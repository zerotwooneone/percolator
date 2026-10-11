using Percolator.Domain.Common;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public readonly record struct InvitationId
{
    public const int MaxLength = 128;

    public string Value { get; }

    public InvitationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("InvitationId cannot be empty or whitespace.", nameof(value));
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException($"InvitationId exceeds maximum length of {MaxLength} characters.", nameof(value));
        }

        Value = value;
    }

    public static DomainResult<InvitationId> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainResult<InvitationId>.Failure(new DomainError("INVALID_INVITATION_ID", "InvitationId cannot be empty or whitespace."));
        }

        if (value.Length > MaxLength)
        {
            return DomainResult<InvitationId>.Failure(new DomainError("INVALID_INVITATION_ID", $"InvitationId exceeds maximum length of {MaxLength} characters."));
        }

        return DomainResult<InvitationId>.Success(new InvitationId(value));
    }

    public static InvitationId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value ?? string.Empty;
}
