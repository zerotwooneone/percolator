using System;
using MediatR;

namespace Percolator.Application.Cli;

public record CreateSelfIdentityCommand(string Name, Guid? PeerId) : IRequest<int>;
