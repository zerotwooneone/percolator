using R3;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel
{
    private readonly MainDefaultViewmodel _mainDefaultViewmodel;
    private readonly SettingsViewmodel _settingsViewmodel;
    public BindableReactiveProperty<object?> SelectedViewmodel { get; } = new();
    public BaseCommand SettingsCommand { get; }
    public BaseCommand ChatCommand { get; }

    public MainWindowViewmodel(
        MainDefaultViewmodel mainDefaultViewmodel,
        SettingsViewmodel settingsViewmodel)
    {
        _mainDefaultViewmodel = mainDefaultViewmodel;
        _settingsViewmodel = settingsViewmodel;
        SelectedViewmodel.Value = _mainDefaultViewmodel;
        SettingsCommand = new BaseCommand(_ => SelectedViewmodel.Value = _settingsViewmodel);
        ChatCommand = new BaseCommand(_ => SelectedViewmodel.Value = _mainDefaultViewmodel);
    }
}