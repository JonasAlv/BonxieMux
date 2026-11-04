using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;

namespace Skua.App.Avalonia.ViewModels;

public class ScriptStatsViewModel : ObservableObject, IManagedWindow
{
    public ScriptStatsViewModel(IScriptBotStats scriptStats)
    {
        ScriptStats = scriptStats;
        ResetStatsCommand = new RelayCommand(ScriptStats.Reset);
        GetSpaceCommand = new RelayCommand(ScriptStats.GetSpace);
    }

    public IScriptBotStats ScriptStats { get; }
    public IRelayCommand ResetStatsCommand { get; }
    public IRelayCommand GetSpaceCommand { get; }
    public string Title => "Stats";
    public int Width => 440;
    public int Height => 320;
    public bool CanResize => true;
}
