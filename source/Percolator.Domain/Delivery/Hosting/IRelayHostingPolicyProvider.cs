namespace Percolator.Domain.Delivery.Hosting;

using System;

public interface IRelayHostingPolicyProvider
{
    RelayHostingPolicy GetPolicy();
}

public sealed class RelayHostingPolicyHolder : IRelayHostingPolicyProvider
{
    private volatile RelayHostingPolicy _policy;

    public RelayHostingPolicyHolder(RelayHostingPolicy? initialPolicy = null)
    {
        _policy = initialPolicy ?? RelayHostingPolicy.Default;
    }

    public RelayHostingPolicy GetPolicy() => _policy;

    public void UpdatePolicy(RelayHostingPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }
}
