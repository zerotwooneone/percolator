namespace Percolator.Application.Manifests;

public class SharedDirectoryProvider : ISharedDirectoryProvider
{
    private readonly List<string> _sharedDirectories;

    public SharedDirectoryProvider()
    {
        // For now, hardcode a safe, user-specific directory.
        // In the future, this would come from a configuration file.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var percolatorShares = Path.Combine(userProfile, "PercolatorShares");

        // Ensure the directory exists
        if (!Directory.Exists(percolatorShares))
        {
            Directory.CreateDirectory(percolatorShares);
        }

        _sharedDirectories = new List<string> { percolatorShares };
    }

    public IEnumerable<string> GetSharedDirectories()
    {
        return _sharedDirectories;
    }
}
