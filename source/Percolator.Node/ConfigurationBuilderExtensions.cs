using Microsoft.Extensions.Configuration;
using System.IO;
using System.Reflection;

namespace Percolator.Node;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddNode(this IConfigurationBuilder configurationBuilder)
    {
        // Get the directory where the executable is located
        var executableDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        
        // Only look for appsettings.json in the executable directory
        if (executableDirectory != null)
        {
            var executablePathConfig = Path.Combine(executableDirectory, "appsettings.json");
            configurationBuilder.AddJsonFile(executablePathConfig, optional: false, reloadOnChange: true);
        }
        
        return configurationBuilder;
    }
}