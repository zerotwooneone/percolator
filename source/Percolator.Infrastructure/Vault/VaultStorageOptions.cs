namespace Percolator.Infrastructure.Vault;

public sealed class VaultStorageOptions
{
    public string BaseDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Percolator");

    public string HeaderFilePath => Path.Combine(BaseDirectory, "percolator.header");
    public string DatabaseFilePath => Path.Combine(BaseDirectory, "percolator.db");
    public string CacheDirectoryPath => Path.Combine(BaseDirectory, "cache");
}
