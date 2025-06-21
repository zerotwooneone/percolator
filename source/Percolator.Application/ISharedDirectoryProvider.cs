namespace Percolator.Application;

public interface ISharedDirectoryProvider
{
    IEnumerable<string> GetSharedDirectories();
}
