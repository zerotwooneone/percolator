namespace Percolator.Application.Manifests;

public interface ISharedDirectoryProvider
{
    IEnumerable<string> GetSharedDirectories();
}
