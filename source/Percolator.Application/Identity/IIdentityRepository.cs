using Percolator.Identity.Model;
using Percolator.Network;
using System.Threading.Tasks;

namespace Percolator.Application.Identity;

public interface IIdentityRepository
{
    Task<IdentityRecord?> GetIdentityForPublicKeyAsync(PublicKeyHash publicKeyHash);
    Task AssociatePublicKeyWithIdentityAsync(PublicKeyHash publicKeyHash, IdentityRecord identity);
}
