using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Discovery port forwarding to Domain-level IPreKeyBundleProvider for backward compatibility.
/// </summary>
public interface IPreKeyBundleProvider : Percolator.Domain.Delivery.Ports.IPreKeyBundleProvider
{
}
