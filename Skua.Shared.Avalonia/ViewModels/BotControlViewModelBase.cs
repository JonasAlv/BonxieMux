using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;

namespace Skua.Shared.Avalonia.ViewModels;

public class BotControlViewModelBase : ObservableRecipient, IManagedWindow
{
    public string Title { get; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool CanResize { get; }
    public int? X { get; set; }
    public int? Y { get; set; }

    public BotControlViewModelBase(string title, int width, int height, bool canResize = true, int? x = null, int? y = null)
    {
        Title = title;
        Width = width;
        Height = height;
        CanResize = canResize;
        X = x;
        Y = y;
    }

    public BotControlViewModelBase(string title)
    {
        Title = title;
        Width = 800;
        Height = 450;
        CanResize = true;
    }
}