using MediatR;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions.Queries;

public record GetRelayHostOptionsQuery(int SelfIdentityId) : IRequest<GetRelayHostOptionsResult>;

public record GetRelayHostOptionsResult(IReadOnlyList<RelayHostOptionDto> Options);

public record RelayHostOptionDto(PeerId PeerId, string DisplayName);
