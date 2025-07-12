using System.Collections.Concurrent;
using System.Security.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public class InMemoryOneTimeKeyProvider : IOneTimeKeyProvider
{
    // This would be populated at startup by a key management service.
    private readonly ConcurrentQueue<ECDiffieHellman> _oneTimeKeys = new();

    public ECDiffieHellman? PopOneTimeKey()
    {
        if (_oneTimeKeys.TryDequeue(out var key))
        {
            return key;
        }
        return null;
    }

    // Method to populate the queue, intended for use at application startup.
    public void AddKeys(IEnumerable<ECDiffieHellman> keys)
    {
        foreach (var key in keys)
        {
            _oneTimeKeys.Enqueue(key);
        }
    }
}
