using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace Skua.Shared.Avalonia.ViewModels;

public partial class ScriptItemViewModel : ObservableObject
{
    public ScriptItemViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Status = "Queued";
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDone), nameof(IsStopped), nameof(IsProblem))]
    private string _status;

    // For the Scheduler's status chip: running green, stopped amber, problems red, done dimmed
    public bool IsRunning => Status == "Running";
    public bool IsDone => Status == "Completed";
    public bool IsStopped => Status == "Stopped";
    public bool IsProblem => Status is "Failed" or "Crashed" or "File Not Found" or "Disconnected";

    [ObservableProperty]
    private string _duration = "00:00";

    public System.Guid Id { get; set; } = System.Guid.NewGuid();

    public string Storage => $"Queue_{System.IO.Path.GetFileNameWithoutExtension(Path)}_{Id}";
}
