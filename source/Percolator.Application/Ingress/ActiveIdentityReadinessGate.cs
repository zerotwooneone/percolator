using System;
using Percolator.Application.Identity;

namespace Percolator.Application.Ingress;

public sealed class ActiveIdentityReadinessGate : IIngressReadinessGate
{
    private readonly IActiveIdentityAccessor _activeIdentity;

    public ActiveIdentityReadinessGate(IActiveIdentityAccessor activeIdentity)
    {
        _activeIdentity = activeIdentity ?? throw new ArgumentNullException(nameof(activeIdentity));
    }

    public void EnsureReady()
    {
        if (!_activeIdentity.IsActive)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
    }
}
