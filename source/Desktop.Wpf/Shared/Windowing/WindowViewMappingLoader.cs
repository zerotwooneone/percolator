using System.Collections;
using System.Windows;

namespace Desktop.Wpf.Shared.Windowing;

public static class WindowViewMappingLoader
{
    public static void LoadFromResource(IWindowViewRegistry registry, Uri resourceUri)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (resourceUri is null) throw new ArgumentNullException(nameof(resourceUri));

        var obj = Application.LoadComponent(resourceUri);
        if (obj is not ResourceDictionary dict)
            throw new InvalidOperationException($"View mapping resource did not load as a ResourceDictionary: {resourceUri}");

        var added = 0;
        foreach (DictionaryEntry entry in dict)
        {
            Type? vmType = entry.Key as Type;
            Type? windowType = entry.Value as Type;

            if (vmType is null && entry.Key is string keyStr)
            {
                vmType = Type.GetType(keyStr, throwOnError: false);
            }
            if (windowType is null && entry.Value is string valStr)
            {
                windowType = Type.GetType(valStr, throwOnError: false);
            }

            if (vmType is not null && windowType is not null && typeof(Window).IsAssignableFrom(windowType))
            {
                registry.Register(vmType, windowType);
                added++;
            }
        }

        if (added == 0)
            throw new InvalidOperationException($"No valid ViewModel->Window mappings were found in {resourceUri}. Ensure keys and values are x:Type entries and the file Build Action is Resource.");
    }
}
