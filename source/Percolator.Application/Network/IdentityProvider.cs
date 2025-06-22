using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Network;
using System;

namespace Percolator.Application.Network;

public class IdentityProvider : IIdentityProvider
{
    private readonly IIdentityService _identityService;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<IdentityProvider> _logger;

    public IdentityProvider(IIdentityService identityService, ActiveIdentityContext activeIdentityContext, ILogger<IdentityProvider> logger)
    {
        _identityService = identityService;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public string GetThumbprint()
    {
        if (string.IsNullOrEmpty(_activeIdentityContext.CurrentIdentityName))
        {
            var ex = new InvalidOperationException("The active identity has not been set. The node must be run with the --identity option.");
            _logger.LogError(ex, "Cannot get thumbprint without an active identity.");
            throw ex;
        }

        var certificate = _identityService.GetIdentityCertificate(_activeIdentityContext.CurrentIdentityName);
        return certificate.Thumbprint;
    }
}
