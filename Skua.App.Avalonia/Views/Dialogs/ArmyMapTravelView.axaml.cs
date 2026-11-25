using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views.Dialogs;

public partial class ArmyMapTravelView : UserControl
{
    public event Action<int>? JumpHistoryClicked;
    public event Action? ClearHistoryClicked;
    public event Action<int>? JumpPresetClicked;
    public event Action? CreatePresetClicked;
    public event Action<int>? EditPresetClicked;
    public event Action<int>? DeletePresetClicked;

    public ArmyMapTravelView()
    {
        InitializeComponent();
        JumpHistoryButton.Click += (_, _) => JumpHistoryClicked?.Invoke(HistoryPicker.SelectedIndex);
        ClearHistoryButton.Click += (_, _) => ClearHistoryClicked?.Invoke();
        JumpPresetButton.Click += (_, _) => JumpPresetClicked?.Invoke(PresetPicker.SelectedIndex);
        CreatePresetButton.Click += (_, _) => CreatePresetClicked?.Invoke();
        EditPresetButton.Click += (_, _) => EditPresetClicked?.Invoke(PresetPicker.SelectedIndex);
        DeletePresetButton.Click += (_, _) => DeletePresetClicked?.Invoke(PresetPicker.SelectedIndex);
    }

    public void RefreshHistory(IEnumerable<string> items)
    {
        List<string> list = items.ToList();
        HistoryPicker.ItemsSource = list;
        HistoryPicker.SelectedIndex = list.Count > 0 ? 0 : -1;
        JumpHistoryButton.IsEnabled = list.Count > 0;
        ClearHistoryButton.IsEnabled = list.Count > 0;
    }

    public void RefreshPresets(IEnumerable<string> items, int selectedIndex = 0)
    {
        List<string> list = items.ToList();
        PresetPicker.ItemsSource = list;
        PresetPicker.SelectedIndex = list.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, list.Count - 1);
        bool hasPresets = list.Count > 0;
        JumpPresetButton.IsEnabled = hasPresets;
        EditPresetButton.IsEnabled = hasPresets;
        DeletePresetButton.IsEnabled = hasPresets;
    }
}
