using System;
using System.IO;

namespace Percolator.Identity;

public record IdentityConfiguration
{
    public string IdentityName { get; }
    public string BasePath { get; }

    public IdentityConfiguration(string identityName)
    {
        IdentityName = identityName;
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        BasePath = Path.Combine(appDataPath, "Percolator", identityName);
    }
}
