using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed record CreateGroupCommand(
    int SelfIdentityId,
    IReadOnlyList<PeerId> InitialMembers,
    string? GroupName
) : IRequest<Guid>;
