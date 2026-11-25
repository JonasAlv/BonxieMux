using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Skua.Shared.Avalonia.Theming;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Globalization;

namespace Skua.Shared.Avalonia.Controls.Options;

/// <summary>
/// Unified value editor for both <see cref="OptionContainerItemViewModel"/> and <see cref="CommandOptionItemViewModel"/>.
/// </summary>
public class OptionValueEditorControl : ContentControl
{
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RebuildEditor();
    }

    private void RebuildEditor()
    {
        if (DataContext is OptionContainerItemViewModel item)
        {
            Content = BuildOptionContainerEditor(item);
            return;
        }

        if (DataContext is CommandOptionItemViewModel commandOption)
        {
            Content = BuildCommandOptionEditor(commandOption);
            return;
        }

        Content = null;
    }

    private static Control BuildOptionContainerEditor(OptionContainerItemViewModel item)
    {
        if (item.IsBool)
        {
            CheckBox checkBox = new()
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, UiTokens.GetDouble(UiTokens.UiGapXs, 4), 0)
            };
            checkBox[!CheckBox.IsCheckedProperty] = new Binding(nameof(OptionContainerItemViewModel.BoolValue))
            {
                Mode = BindingMode.TwoWay
            };
            return checkBox;
        }

        if (item.IsEnum)
        {
            ComboBox comboBox = new()
            {
                ItemsSource = item.EnumValues,
                MinWidth = 160,
                MaxWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            UiTokens.Bind(comboBox, Layoutable.HeightProperty, UiTokens.UiControlHeight);
            comboBox[!ComboBox.SelectedItemProperty] = new Binding(nameof(OptionContainerItemViewModel.SelectedValue))
            {
                Mode = BindingMode.TwoWay
            };
            return comboBox;
        }

        TextBox textBox = new()
        {
            MinWidth = 140,
            MaxWidth = 220,
            Padding = new Thickness(8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(textBox, Layoutable.HeightProperty, UiTokens.UiControlHeight);
        textBox[!TextBox.TextProperty] = new Binding(nameof(OptionContainerItemViewModel.TextValue))
        {
            Mode = BindingMode.TwoWay
        };
        return textBox;
    }

    private static Control? BuildCommandOptionEditor(CommandOptionItemViewModel option)
    {
        if (option.DisplayType == typeof(bool))
        {
            ToggleSwitch toggle = new()
            {
                IsChecked = option.Value is bool value && value,
                OnContent = string.Empty,
                OffContent = string.Empty,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked is bool value && option.Command.CanExecute(value))
                    option.Command.Execute(value);
            };
            return toggle;
        }

        if (option.DisplayType == typeof(int))
        {
            bool isPercentage = option.Tag == "ServerLatencyNearThresholdPercent";
            int minimum = 50;
            int maximum = isPercentage ? 95 : 2000;
            string suffix = isPercentage ? "%" : "ms";

            TextBox input = new()
            {
                Text = Convert.ToString(option.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                PlaceholderText = $"{minimum}–{maximum}",
                Width = 104,
                Padding = new Thickness(8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Right,
            };
            UiTokens.Bind(input, Layoutable.HeightProperty, UiTokens.UiControlHeight);
            input.TextChanged += (_, _) =>
            {
                if (!input.IsKeyboardFocusWithin ||
                    !int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                    value < minimum || value > maximum)
                    return;

                string commandValue = value.ToString(CultureInfo.InvariantCulture);
                if (option.Command.CanExecute(commandValue))
                {
                    option.Command.Execute(commandValue);
                    option.Value = value;
                }
            };
            input.LostFocus += (_, _) =>
            {
                if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                    value < minimum || value > maximum)
                    input.Text = Convert.ToString(option.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            };

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiTokens.GetDouble(UiTokens.UiGapSm, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    input,
                    new TextBlock
                    {
                        Text = suffix,
                        MinWidth = 22,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                }
            };
        }

        return null;
    }
}
