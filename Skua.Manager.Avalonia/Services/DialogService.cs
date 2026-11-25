using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Core.Interfaces.ViewModels;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Manager.Avalonia.ViewModels;
using Skua.Shared.Avalonia.Services;
using Skua.Shared.Avalonia.Theming;
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
        JobEditorDialogViewModel jobEditor => ShowJobEditorDialog(jobEditor, title),
        _ => null
    };

    private static bool? ShowJobEditorDialog(JobEditorDialogViewModel vm, string title)
    {
        ModalDialogWindow dialog = CreateModalDialog(title, 640);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = 720;
        dialog.CanResize = true;

        vm.CloseAction = () => dialog.Close();
        dialog.Closed += (_, _) => vm.Cleanup();

        dialog.SetBody(new ContentControl { Content = vm }, new Thickness(0));
        Show(dialog);
        return vm.DialogResult;
    }

    private static bool? ShowAccountEditorDialog(AccountEditorViewModel vm, string title)
    {
        ModalDialogWindow dialog = CreateModalDialog(title, 440);
        double gapBase = UiTokens.GetDouble(UiTokens.UiGapBase, 10);
        double iconMd = UiTokens.GetDouble(UiTokens.UiIconMd, 16);
        double ctrlH = UiTokens.GetDouble(UiTokens.UiControlHeight, 28);

        TextBox usernameBox = new()
        {
            Text = vm.Username,
            PlaceholderText = "Username",
            Margin = new Thickness(0, 0, 0, gapBase)
        };

        TextBox displayNameBox = new()
        {
            Text = vm.DisplayName,
            PlaceholderText = "Display Name (optional)",
            Margin = new Thickness(0, 0, 0, gapBase)
        };

        TextBox passwordBox = new()
        {
            Text = vm.Password,
            PlaceholderText = "Password",
            PasswordChar = '*',
            Padding = new Thickness(8, 6, 34, 6)
        };

        Material.Icons.Avalonia.MaterialIcon eyeIcon = new()
        {
            Kind = Material.Icons.MaterialIconKind.EyeOutline,
            Width = iconMd,
            Height = iconMd
        };

        Button eyeButton = new()
        {
            Content = eyeIcon,
            Classes = { "icon" },
            Width = ctrlH,
            Height = ctrlH,
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
            Margin = new Thickness(0, 0, 0, gapBase),
            Children = { passwordBox, eyeButton }
        };

        TextBox tagsBox = new()
        {
            Text = vm.Tags,
            PlaceholderText = "Tags (comma-separated, e.g. farm, daily)"
        };

        bool? result = null;
        dialog.AddButton("Cancel", () => { result = false; dialog.Close(); }, isCancel: true);
        dialog.AddButton("Save", () =>
        {
            vm.Username = usernameBox.Text ?? string.Empty;
            vm.DisplayName = displayNameBox.Text ?? string.Empty;
            vm.Password = passwordBox.Text ?? string.Empty;
            vm.Tags = tagsBox.Text ?? string.Empty;
            result = true;
            dialog.Close();
        }, isPrimary: true, isDefault: true);

        double gapXs = UiTokens.GetDouble(UiTokens.UiGapXs, 4);
        TextBlock Label(string text)
        {
            TextBlock tb = ModalDialogWindow.CreateFieldLabel(text);
            tb.Margin = new Thickness(0, 0, 0, gapXs);
            return tb;
        }

        StackPanel formPanel = new()
        {
            Children =
            {
                Label("Username"),
                usernameBox,
                Label("Display Name"),
                displayNameBox,
                Label("Password"),
                passwordContainer,
                Label("Tags"),
                tagsBox
            }
        };

        dialog.SetBody(formPanel);
        Show(dialog);
        return result;
    }

    private static bool? ShowSelectGroupDialog(SelectGroupDialogViewModel vm, string title)
    {
        ModalDialogWindow dialog = CreateModalDialog(title);
        ComboBox groups = new()
        {
            ItemsSource = vm.Groups,
            SelectedItem = vm.SelectedGroup,
            ItemTemplate = new FuncDataTemplate<GroupItemViewModel>((g, _) =>
                new TextBlock { Text = g?.Name ?? string.Empty }, true)
        };

        bool? result = null;
        dialog.AddButton("Cancel", () => { result = false; dialog.Close(); }, isCancel: true);
        dialog.AddButton("OK", () =>
        {
            vm.SelectedGroup = groups.SelectedItem as GroupItemViewModel;
            result = vm.SelectedGroup != null;
            dialog.Close();
        }, isPrimary: true, isDefault: true);

        StackPanel content = new()
        {
            Spacing = UiTokens.GetDouble(UiTokens.UiGapBase, 10),
            Children =
            {
                ModalDialogWindow.CreateBodyText("Select group"),
                groups
            }
        };
        dialog.SetBody(content);
        Show(dialog);
        return result;
    }

    private static bool? ShowSelectAccountDialog(SelectAccountDialogViewModel vm, string title)
    {
        ModalDialogWindow dialog = CreateModalDialog(title);
        TextBox searchBox = new()
        {
            PlaceholderText = "Search accounts...",
            Text = vm.SearchText
        };
        searchBox.TextChanged += (_, _) => vm.SearchText = searchBox.Text ?? string.Empty;

        ListBox accounts = new()
        {
            MaxHeight = UiTokens.GetDouble(UiTokens.UiPopupMaxHeight, 260),
            ItemsSource = vm.FilteredAccounts,
            SelectedItem = vm.SelectedAccount,
            ItemTemplate = new FuncDataTemplate<IAccountItemViewModel>((a, _) =>
                new TextBlock { Text = a?.DisplayOrUsername ?? string.Empty }, true)
        };
        accounts.SelectionChanged += (_, _) => vm.SelectedAccount = accounts.SelectedItem as IAccountItemViewModel;

        bool? result = null;
        dialog.AddButton("Cancel", () => { result = false; dialog.Close(); }, isCancel: true);
        dialog.AddButton("OK", () =>
        {
            result = vm.SelectedAccount != null;
            dialog.Close();
        }, isPrimary: true, isDefault: true);

        StackPanel content = new()
        {
            Spacing = UiTokens.GetDouble(UiTokens.UiGapBase, 10),
            Children =
            {
                ModalDialogWindow.CreateBodyText("Select replacement account"),
                searchBox,
                accounts
            }
        };
        dialog.SetBody(content);
        Show(dialog);
        return result;
    }

}
