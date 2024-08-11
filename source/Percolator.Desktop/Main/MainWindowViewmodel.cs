using R3;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel
{
    private readonly MainDefaultViewmodel _mainDefaultViewmodel;
    public BindableReactiveProperty<object?> SelectedViewmodel { get; } = new();

    public MainWindowViewmodel(MainDefaultViewmodel mainDefaultViewmodel)
    {
        _mainDefaultViewmodel = mainDefaultViewmodel;
        SelectedViewmodel.Value = _mainDefaultViewmodel;
    }
}