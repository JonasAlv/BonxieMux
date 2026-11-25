using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace Skua.App.Avalonia.Views.Popups;

public partial class AutoQuickPopupView : UserControl
{
    private bool _suppressSync;

    public event Action? AutoAttackClicked;
    public event Action? AutoHuntClicked;
    public event Action? AutoStopClicked;
    public event Action<string>? ClassSelected;
    public event Action<string>? ModeSelected;
    public event Action<string>? ManualMapIdsCommitted;

    public AutoQuickPopupView()
    {
        InitializeComponent();

        AutoAttackBtn.Click += (_, _) => AutoAttackClicked?.Invoke();
        AutoHuntBtn.Click += (_, _) => AutoHuntClicked?.Invoke();
        AutoStopBtn.Click += (_, _) => AutoStopClicked?.Invoke();

        AutoClassBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressSync && AutoClassBox.SelectedItem is string cls)
                ClassSelected?.Invoke(cls);
        };

        AutoModeBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressSync && AutoModeBox.SelectedItem is string mode)
                ModeSelected?.Invoke(mode);
        };

        AutoMapIdsBox.LostFocus += (_, _) =>
        {
            if (!_suppressSync)
                ManualMapIdsCommitted?.Invoke(AutoMapIdsBox.Text ?? string.Empty);
        };
    }

    public void ApplyState(
        bool isRunning,
        List<string> classes,
        string? selectedClass,
        List<string> modes,
        string? selectedMode,
        string manualMapIds)
    {
        _suppressSync = true;
        try
        {
            AutoRunningBadge.IsVisible = isRunning;
            AutoAttackBtn.IsVisible = !isRunning;
            AutoHuntBtn.IsVisible = !isRunning;
            AutoStopBtn.IsVisible = isRunning;

            AutoClassBox.ItemsSource = classes;
            AutoClassBox.SelectedItem = selectedClass;
            AutoModeBox.ItemsSource = modes;
            AutoModeBox.SelectedItem = selectedMode;
            AutoMapIdsBox.Text = manualMapIds;
        }
        finally
        {
            _suppressSync = false;
        }
    }
}
