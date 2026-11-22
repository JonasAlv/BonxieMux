using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Core.Interfaces.ViewModels;
using Skua.Manager.Avalonia.Controls;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.Services;
using System;

namespace Skua.Manager.Avalonia.Services;

public class DialogService : DialogServiceBase
{
    public override void ShowMessageBox(string message, string caption)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption));
            return;
        }
        if (message.Length <= 140 && !message.Contains('\n') && ToastHost.Current is { } toasts && IsManagerVisible())
        {
            toasts.Push(message);
            return;
        }
        _ = ShowButtonsDialog(caption, message, ["OK"]);
    }

    private static bool IsManagerVisible() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true, WindowState: not WindowState.Minimized } };

    protected override bool? ShowAppSpecificDialog(object viewModel, string title) => viewModel switch
    {
        AccountEditorViewModel accountEditor => ShowAccountEditorDialog(accountEditor, title),
        SelectGroupDialogViewModel selectGroup => ShowSelectGroupDialog(selectGroup, title),
        SelectAccountDialogViewModel selectAccount => ShowSelectAccountDialog(selectAccount, title),
        _ => null
    };

    private static bool? ShowAccountEditorDialog(AccountEditorViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(title);
        dialog.Width = 440;

        TextBox usernameBox = new()
        {
            Text = vm.Username,
            Watermark = "Username",
            Margin = new Thickness(0, 0, 0, 10)
        };

        TextBox displayNameBox = new()
        {
            Text = vm.DisplayName,
            Watermark = "Display Name (optional)",
            Margin = new Thickness(0, 0, 0, 10)
        };

        TextBox passwordBox = new()
        {
            Text = vm.Password,
            Watermark = "Password",
            PasswordChar = '*',
            Padding = new Thickness(8, 6, 34, 6)
        };

        Material.Icons.Avalonia.MaterialIcon eyeIcon = new()
        {
            Kind = Material.Icons.MaterialIconKind.EyeOutline,
            Width = 16,
            Height = 16
        };

        Button eyeButton = new()
        {
            Content = eyeIcon,
            Classes = { "icon" },
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };
        ToolTip.SetTip(eyeButton, "Show/Hide Password");

        bool isPswVisible = false;
        eyeButton.Click += (_, _) =>
        {
            isPswVisible = !isPswVisible;
            passwordBox.PasswordChar = isPswVisible ? '\0' : '*';
            eyeIcon.Kind = isPswVisible ? Material.Icons.MaterialIconKind.EyeOffOutline : Material.Icons.MaterialIconKind.EyeOutline;
        };

        Grid passwordContainer = new()
        {
            Margin = new Thickness(0, 0, 0, 10),
            Children = { passwordBox, eyeButton }
        };

        TextBox tagsBox = new()
        {
            Text = vm.Tags,
            Watermark = "Tags (comma-separated, e.g. farm, daily)",
            Margin = new Thickness(0, 0, 0, 16)
        };

        bool? result = null;
        Button ok = new() { Content = "Save", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        ok.IsDefault = true;

        ok.Click += (_, _) =>
        {
            vm.Username = usernameBox.Text ?? string.Empty;
            vm.DisplayName = displayNameBox.Text ?? string.Empty;
            vm.Password = passwordBox.Text ?? string.Empty;
            vm.Tags = tagsBox.Text ?? string.Empty;
            result = true;
            dialog.Close();
        };

        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        StackPanel formPanel = new()
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Username", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), FontSize = 12 },
                usernameBox,
                new TextBlock { Text = "Display Name", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), FontSize = 12 },
                displayNameBox,
                new TextBlock { Text = "Password", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), FontSize = 12 },
                passwordContainer,
                new TextBlock { Text = "Tags", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4), FontSize = 12 },
                tagsBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        dialog.Content = WrapWithShell(title, formPanel);
        Show(dialog);
        return result;
    }


    private static bool? ShowSelectGroupDialog(SelectGroupDialogViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(title);
        ComboBox groups = new()
        {
            ItemsSource = vm.Groups,
            SelectedItem = vm.SelectedGroup,
            ItemTemplate = new FuncDataTemplate<GroupItemViewModel>((g, _) =>
                new TextBlock { Text = g?.Name ?? string.Empty }, true)
        };

        bool? result = null;
        Button ok = new() { Content = "OK", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            vm.SelectedGroup = groups.SelectedItem as GroupItemViewModel;
            result = vm.SelectedGroup != null;
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        StackPanel content = new()
        {
            Margin = new Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Select group" },
                groups,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
        dialog.Content = WrapWithShell(title, content);
        Show(dialog);
        return result;
    }

    private static bool? ShowSelectAccountDialog(SelectAccountDialogViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(title);
        TextBox searchBox = new()
        {
            Watermark = "Search accounts...",
            Text = vm.SearchText
        };
        searchBox.TextChanged += (_, _) =>
        {
            vm.SearchText = searchBox.Text ?? string.Empty;
        };

        ListBox accounts = new()
        {
            MaxHeight = 260,
            ItemsSource = vm.FilteredAccounts,
            SelectedItem = vm.SelectedAccount,
            ItemTemplate = new FuncDataTemplate<IAccountItemViewModel>((a, _) =>
            {
                return new TextBlock { Text = a?.DisplayOrUsername ?? string.Empty };
            }, true)
        };
        accounts.SelectionChanged += (_, _) =>
        {
            vm.SelectedAccount = accounts.SelectedItem as IAccountItemViewModel;
        };

        bool? result = null;
        Button ok = new() { Content = "OK", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            result = vm.SelectedAccount != null;
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        StackPanel content = new()
        {
            Margin = new Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Select replacement account" },
                searchBox,
                accounts,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
        dialog.Content = WrapWithShell(title, content);
        Show(dialog);
        return result;
    }

}
