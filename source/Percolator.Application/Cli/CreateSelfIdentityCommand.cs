using System;
using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public record CreateSelfIdentityCommand(string Name, Guid? PeerId) : IRequest<SelfId>;
