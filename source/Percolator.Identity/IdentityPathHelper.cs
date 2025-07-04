using System;
using System.IO;

namespace Percolator.Identity;

public static class IdentityPathHelper
{
    public static string GetBasePath(string identityName)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "Percolator", identityName);
    }
}
