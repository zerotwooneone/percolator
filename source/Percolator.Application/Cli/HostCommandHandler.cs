using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;

namespace Percolator.Application.Cli;

public sealed class HostCommandHandler : IRequestHandler<HostCommand, HostStartupInfo>
{
    private readonly IIdentityOrchestrator _identityOrchestrator;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<HostCommandHandler> _logger;

    public HostCommandHandler(
        IIdentityOrchestrator identityOrchestrator,
        ActiveIdentityContext activeIdentityContext,
        ILogger<HostCommandHandler> logger)
    {
        _identityOrchestrator = identityOrchestrator;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public async Task<HostStartupInfo> Handle(HostCommand request, CancellationToken cancellationToken)
    {
        // Ensure identity is resolved and keys are loaded
        await _identityOrchestrator.ResolveIdentityAsync(request.SelfIdentityName, cancellationToken, fallbackIdentityName: "default").ConfigureAwait(false);

        if (_activeIdentityContext.Keys?.IdentitySigningKey is null)
        {
            throw new InvalidOperationException("Active identity signing key not loaded.");
        }

        var publicKeyB64 = Convert.ToBase64String(_activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo());
        _logger.LogInformation("Hosting with identity '{IdentityName}'", request.SelfIdentityName);
        return new HostStartupInfo(request.SelfIdentityName, publicKeyB64);
    }
}
