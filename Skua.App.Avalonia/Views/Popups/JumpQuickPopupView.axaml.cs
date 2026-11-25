using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace Skua.App.Avalonia.Views.Popups;

public partial class JumpQuickPopupView : UserControl
{
    private bool _suppressSync;

    public event Action? CurrentClicked;
    public event Action<string, string>? JumpRequested;

    public JumpQuickPopupView()
    {
        InitializeComponent();

        JumpCellBox.SelectionChanged += (_, _) =>
        {
            if (_suppressSync || JumpCellBox.SelectedItem is not string cell || string.IsNullOrEmpty(cell))
                return;
            if (cell == "Enter")
                JumpPadBox.SelectedItem = "Spawn";
            else if (JumpPadBox.SelectedItem is not string p || string.IsNullOrEmpty(p))
                JumpPadBox.SelectedItem = "Left";
            string pad = JumpPadBox.SelectedItem as string ?? "Spawn";
            JumpRequested?.Invoke(cell, pad);
        };

        CurrentBtn.Click += (_, _) => CurrentClicked?.Invoke();

        JumpBtn.Click += (_, _) =>
        {
            string cell = JumpCellBox.SelectedItem as string ?? string.Empty;
            string pad = JumpPadBox.SelectedItem as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cell) && !string.IsNullOrWhiteSpace(pad))
                JumpRequested?.Invoke(cell, pad);
        };
    }

    public void ApplyState(
        List<string> cells,
        string? selectedCell,
        List<string> pads,
        string? selectedPad)
    {
        _suppressSync = true;
        try
        {
            JumpCellBox.ItemsSource = cells;
            JumpCellBox.SelectedItem = selectedCell;
            JumpPadBox.ItemsSource = pads;
            JumpPadBox.SelectedItem = selectedPad;
        }
        finally
        {
            _suppressSync = false;
        }
    }
}
