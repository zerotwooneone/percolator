using MediatR;

namespace Percolator.Application.Cli;

public sealed record HostStartupInfo(string SelfIdentityName, string PublicKeyB64);

public sealed record HostCommand(string SelfIdentityName) : IRequest<HostStartupInfo>;
