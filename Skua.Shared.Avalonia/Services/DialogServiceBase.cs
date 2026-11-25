using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls.Options;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.Theming;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Linq;
using System.Threading;

namespace Skua.Shared.Avalonia.Services;

public abstract class DialogServiceBase : IDialogService
{
    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel));
        return ShowDialogInternal(viewModel, DefaultTitle(viewModel));
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel, title));
        return ShowDialogInternal(viewModel, title);
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel, callback));
        bool? result = ShowDialogInternal(viewModel, DefaultTitle(viewModel));
        if (result == true)
            callback?.Invoke(viewModel);
        return result;
    }

    public void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ShowDialog(optionContainer, callback));
            return;
        }
        OptionContainerViewModel vm = new(optionContainer);
        bool? result = ShowOptionContainerDialog(vm, "Script Options");
        if (result == true)
            callback?.Invoke(vm);
    }

    public virtual void ShowMessageBox(string message, string caption)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption));
            return;
        }
        if (message.Length <= 180 && !message.Contains('\n') && ToastHost.Current is { } toasts)
        {
            toasts.Push(string.IsNullOrWhiteSpace(caption) ? message : $"{caption}: {message}");
            return;
        }
        _ = ShowButtonsDialog(caption, message, ["OK"]);
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption, yesAndNo));
        return ShowMessageBoxInternal(message, caption, yesAndNo);
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption, buttons));
        string[] actualButtons = buttons.Length == 0 ? ["OK"] : buttons;
        string clicked = ShowButtonsDialog(caption, message, actualButtons);
        int index = Array.IndexOf(actualButtons, clicked);
        return index < 0 ? DialogResult.Cancelled : new DialogResult(clicked, index);
    }

    public IInputDialogViewModel CreateInputDialog(string title, string dialogHint)
        => new InputDialogViewModel(title, dialogHint);

    protected virtual bool? ShowAppSpecificDialog(object viewModel, string title) => null;

    protected virtual string ResolveOptionContainerTitle(string title) => title;

    private static string DefaultTitle(object viewModel)
        => viewModel is DialogViewModelBase { Title: { Length: > 0 } title } ? title : viewModel.GetType().Name;

    private bool? ShowDialogInternal(object viewModel, string title)
    {
        if (ShowAppSpecificDialog(viewModel, title) is { } handled)
            return handled;

        return viewModel switch
        {
            OptionContainerViewModel options => ShowOptionContainerDialog(options, string.IsNullOrWhiteSpace(title) || title == nameof(OptionContainerViewModel) ? options.Title : title),
            IOptionContainerViewModel optionsVm => ShowOptionContainerDialog(optionsVm as OptionContainerViewModel ?? new OptionContainerViewModel(optionsVm.Container), title),
            InputDialogViewModel input => ShowInputDialog(input, title),
            MessageBoxDialogViewModel messageBox => ShowMessageBoxInternal(messageBox.Message, messageBox.Title, messageBox.YesAndNo),
            CustomDialogViewModel custom => ShowCustomDialog(custom, title),
            _ => ShowFallbackDialog(viewModel.GetType().Name, title)
        };
    }

    private static bool? ShowInputDialog(InputDialogViewModel vm, string title)
    {
        ModalDialogWindow dialog = CreateModalDialog(title);
        TextBox input = new() { Text = vm.DialogTextInput, PlaceholderText = vm.TextBoxHint };
        if (vm.NumberOnly)
        {
            input.TextChanged += (_, _) =>
            {
                string digitsOnly = new(input.Text?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
                if (input.Text != digitsOnly)
                {
                    int caret = Math.Min(input.CaretIndex, digitsOnly.Length);
                    input.Text = digitsOnly;
                    input.CaretIndex = caret;
                }
            };
        }

        bool? result = null;
        dialog.AddButton("Cancel", () => { result = false; dialog.Close(); }, isCancel: true);
        dialog.AddButton("OK", () =>
        {
            vm.DialogTextInput = input.Text ?? string.Empty;
            result = true;
            dialog.Close();
        }, isPrimary: true, isDefault: true);

        StackPanel content = new()
        {
            Spacing = UiTokens.GetDouble(UiTokens.UiGapLg, 12),
            Children =
            {
                ModalDialogWindow.CreateBodyText(vm.DialogHint),
                input
            }
        };

        dialog.SetBody(content);
        Show(dialog);
        return result;
    }

    private bool? ShowOptionContainerDialog(OptionContainerViewModel vm, string title)
    {
        string resolvedTitle = ResolveOptionContainerTitle(title);
        ModalDialogWindow dialog = CreateModalDialog(resolvedTitle, 560);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.MinWidth = 460;
        dialog.MinHeight = 420;
        dialog.CanResize = true;

        double maxScreenHeight = 720;
        try
        {
            var screen = WindowPlacementHelper.GetTargetScreen(dialog);
            if (screen != null)
                maxScreenHeight = Math.Max(420, screen.WorkingArea.Height * 0.85);
        }
        catch { }
        dialog.MaxHeight = maxScreenHeight;
        dialog.Height = Math.Min(560, maxScreenHeight);

        OptionContainerUserControl content = new() { DataContext = vm };

        Button resetBtn = new() { Content = "Reset Defaults", Classes = { "skua-button" } };
        resetBtn.Click += (_, _) => vm.ResetAllToDefaults();
        dialog.SetLeftFooter(resetBtn);

        bool? result = null;
        dialog.AddButton("Cancel", () => { result = false; dialog.Close(); }, isCancel: true);
        dialog.AddButton("Save", () => { result = true; dialog.Close(); }, isPrimary: true, isDefault: true);

        dialog.SetBody(content, new Thickness(0));
        Show(dialog);
        return result;
    }

    private static bool? ShowCustomDialog(CustomDialogViewModel vm, string title)
    {
        string clicked = ShowButtonsDialog(title, vm.Message, vm.Buttons.ToArray());
        int index = vm.Buttons.IndexOf(clicked);
        vm.Result = index < 0 ? DialogResult.Cancelled : new DialogResult(clicked, index);
        return index >= 0;
    }

    private static bool? ShowFallbackDialog(string typeName, string title)
    {
        _ = ShowButtonsDialog(title, $"Dialog view: {typeName}", ["OK"]);
        return true;
    }

    private static bool? ShowMessageBoxInternal(string message, string caption, bool yesAndNo)
    {
        string[] buttons = yesAndNo ? ["No", "Yes"] : ["OK"];
        string clicked = ShowButtonsDialog(caption, message, buttons);
        return clicked == "Yes" || clicked == "OK";
    }

    protected static string ShowButtonsDialog(string title, string message, string[] buttons)
    {
        double width = (message.Length > 200 || message.Contains('\n')) ? 560 : 460;
        ModalDialogWindow dialog = CreateModalDialog(title, width);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.CanResize = true;

        double maxScreenHeight = 720;
        var screen = WindowPlacementHelper.GetTargetScreen(dialog);
        if (screen != null)
            maxScreenHeight = Math.Max(160, screen.WorkingArea.Height / screen.Scaling * 0.85);
        dialog.MinHeight = 160;
        dialog.MaxHeight = maxScreenHeight;

        SelectableTextBlock label = new()
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        UiTokens.Bind(label, SelectableTextBlock.FontSizeProperty, UiTokens.UiFontBody);
        label.Measure(new Size(dialog.Width - 34, double.PositiveInfinity));
        dialog.Height = Math.Min(maxScreenHeight, Math.Max(dialog.MinHeight, label.DesiredSize.Height + 112));

        ScrollViewer scrollViewer = new()
        {
            Content = label,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        Button copyButton = new() { Content = "Copy", Classes = { "skua-button", "w84" } };
        copyButton.Click += async (_, _) =>
        {
            if (dialog.Clipboard != null)
            {
                await dialog.Clipboard.SetTextAsync(message);
                copyButton.Content = "Copied!";
            }
        };
        dialog.SetLeftFooter(copyButton);

        string clicked = string.Empty;
        foreach (string text in buttons)
        {
            bool isPrimary = text == "OK" || text == "Yes";
            bool isCancel = text == "Cancel" || text == "No";
            dialog.AddButton(text, () => { clicked = text; dialog.Close(); }, isPrimary: isPrimary, isDefault: isPrimary, isCancel: isCancel);
        }

        dialog.SetBody(scrollViewer);
        Show(dialog);
        return clicked;
    }

    protected static ModalDialogWindow CreateModalDialog(string title, double width = 420) => new(title, width);

    protected static Window CreateBaseDialog(string title) => CreateModalDialog(title);

    protected static Control WrapWithShell(string title, Control content) =>
        ShellWindowHelper.CreateShellContent(title, content);

    protected static void Show(Window dialog)
    {
        WindowPlacementHelper.CenterOnActiveScreen(dialog);
        ShowAndBlock(dialog, GetOwner());
    }

    private static Window? GetOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        Window? activeVisible = desktop.Windows.FirstOrDefault(w => w.IsVisible && w.IsActive && w.Position.X > -10000);
        if (activeVisible != null)
            return activeVisible;

        Window? visible = desktop.Windows.LastOrDefault(w => w.IsVisible && w.Position.X > -10000);
        if (visible != null)
            return visible;

        return (desktop.MainWindow?.IsVisible == true && desktop.MainWindow.Position.X > -10000) ? desktop.MainWindow : null;
    }

    private static void ShowAndBlock(Window dialog, Window? owner)
    {
        using CancellationTokenSource closedCts = new();
        void OnClosed(object? _, EventArgs __) => closedCts.Cancel();
        void OnOpened(object? _, EventArgs __) => FocusDefaultDialogAction(dialog);

        dialog.Closed += OnClosed;
        dialog.Opened += OnOpened;
        if (owner != null)
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show();

        try
        {
            Dispatcher.UIThread.MainLoop(closedCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            dialog.Closed -= OnClosed;
            dialog.Opened -= OnOpened;
        }
    }

    private static void FocusDefaultDialogAction(Window dialog)
    {
        dialog.Activate();
        dialog.Focus();
        dialog.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.IsDefault)?
            .Focus();
    }
}
