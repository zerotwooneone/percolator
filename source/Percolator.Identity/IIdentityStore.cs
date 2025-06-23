namespace Percolator.Identity;

using System.Collections.Generic;
using System.Threading.Tasks;
using Percolator.Identity.Model;
using Identity = Percolator.Identity.Model.Identity;

/// <summary>
/// Defines the contract for storing and retrieving identities.
/// </summary>
public interface IIdentityStore
{
    /// <summary>
    /// Retrieves an identity by its name.
    /// </summary>
    Task<Identity?> GetIdentityAsync(string identityName);

    /// <summary>
    /// Lists the names of all available identities.
    /// </summary>
    Task<IEnumerable<string>> ListIdentityNamesAsync();

    /// <summary>
    /// Stores an identity.
    /// </summary>
    Task StoreIdentityAsync(Identity identity);

    /// <summary>
    /// Checks if an identity with the given name exists.
    /// </summary>
    Task<bool> IdentityExistsAsync(string identityName);
}
