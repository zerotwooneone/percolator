using Microsoft.Extensions.Options;
using Percolator.Infrastructure;

namespace Percolator.InfrastructureTests.Identity;

public class FileBasedPeerRepositoryTests
{
    private string _storagePath = null!;
    private IOptions<StorageOptions> _storageOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _storagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_storagePath);
        _storageOptions = Options.Create(new StorageOptions { Path = _storagePath });
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_storagePath, true);
    }
}
