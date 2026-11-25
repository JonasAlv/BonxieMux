using Avalonia.Controls;
using System;

namespace Skua.App.Avalonia.Views.Dialogs;

public partial class ArmyMapPresetEditorView : UserControl
{
    public event Action? UseCurrentLocationClicked;

    public string PresetName
    {
        get => NameInput.Text ?? string.Empty;
        set => NameInput.Text = value;
    }

    public string MapName
    {
        get => MapInput.Text ?? string.Empty;
        set => MapInput.Text = value;
    }

    public string RoomNumber
    {
        get => RoomInput.Text ?? string.Empty;
        set => RoomInput.Text = value;
    }

    public string CellName
    {
        get => CellInput.Text ?? string.Empty;
        set => CellInput.Text = value;
    }

    public string PadName
    {
        get => PadInput.Text ?? string.Empty;
        set => PadInput.Text = value;
    }

    public ArmyMapPresetEditorView()
    {
        InitializeComponent();
        UseCurrentLocationButton.Click += (_, _) => UseCurrentLocationClicked?.Invoke();
    }
}
