using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.Views.Dialogs;

public partial class ArmyManagePresetsView : UserControl
{
    public event Action<int>? SelectionChangedIndex;

    public int SelectedIndex => PresetList.SelectedIndex;

    public ArmyManagePresetsView()
    {
        InitializeComponent();
        PresetList.SelectionChanged += (_, _) => SelectionChangedIndex?.Invoke(PresetList.SelectedIndex);
    }

    public void RefreshPresets(IEnumerable<string> items, int selectedIndex = 0)
    {
        List<string> list = items.ToList();
        PresetList.ItemsSource = list;
        PresetList.SelectedIndex = list.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, list.Count - 1);
    }

    public void SetDestinationSummary(string text)
    {
        DestinationText.Text = text;
    }
}
