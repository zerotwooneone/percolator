using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Cryptography;

[Table("SentInvitations")]
public sealed class SentInvitationDbo
{
    public int SelfIdentityId { get; set; }
    public string RequestCorrelationId { get; set; } = string.Empty;

    public Guid SignedPreKeyId { get; set; }
    public Guid? OneTimePreKeyId { get; set; }

    public Guid? TargetPeerId { get; set; }

    public string? TargetDisplayName { get; set; }
    public string? TargetEndpointHost { get; set; }
    public int? TargetEndpointPort { get; set; }

    public int InviteRouteKind { get; set; }
    public Guid? InviteRelayHostPeerId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
