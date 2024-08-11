namespace Percolator.Desktop.Main;

/// <summary>
/// marks a service that needs to be started before the main window
/// </summary>
public interface IPreUiInitializer
{
    //delays the main window until the pre app is complete
    Task PreAppComplete { get; }
}