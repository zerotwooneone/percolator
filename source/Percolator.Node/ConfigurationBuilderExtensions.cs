using Microsoft.Extensions.Configuration;

namespace Percolator.Node;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddNode(this IConfigurationBuilder configurationBuilder)
    {
        return configurationBuilder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    }
}