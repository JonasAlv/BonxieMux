using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Skua.App.Avalonia.Services;

public class WindowService : WindowServiceBase
{
    private readonly Dictionary<Type, Window> _typeWindows = new();

    public override void ShowWindow<TViewModel>(int width, int height) where TViewModel : class =>
        ShowSingleWindow<TViewModel>(width, height);

    public override void ShowWindow<TViewModel>() where TViewModel : class =>
        ShowSingleWindow<TViewModel>(900, 600);

    public override void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        Window window = CreateHostWindow(viewModel, GetWindowTitle(viewModel), 900, 600);
        ShowHostWindow(window);
    }

    protected override void ShowHostWindow(Window window)
    {
        Window? main = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (main != null && main != window && main.IsVisible)
        {
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                CenterRelativeTo(window, main);
                window.Show(main);
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (window.IsVisible)
                    {
                        window.Activate();
                        window.Focus();
                    }
                }, global::Avalonia.Threading.DispatcherPriority.Input);
                return;
            }
            catch
            {
            }
        }

        window.Show();
        window.Activate();
        window.Focus();
    }

    private void ShowSingleWindow<TViewModel>(int width, int height) where TViewModel : class
    {
        if (_typeWindows.TryGetValue(typeof(TViewModel), out Window? open))
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        TViewModel vm = Ioc.Default.GetRequiredService<TViewModel>();
        Window window = CreateHostWindow(vm, GetWindowTitle(vm), width, height);
        _typeWindows[typeof(TViewModel)] = window;
        window.Closed += (_, _) => _typeWindows.Remove(typeof(TViewModel));
        ShowHostWindow(window);
    }

    private static string GetWindowTitle(object viewModel)
    {
        if (viewModel is IManagedWindow managed && !string.IsNullOrWhiteSpace(managed.Title))
            return managed.Title;
        if (viewModel is Skua.Shared.Avalonia.ViewModels.BotControlViewModelBase control && !string.IsNullOrWhiteSpace(control.Title))
            return control.Title;

        string name = viewModel.GetType().Name;
        if (name.EndsWith("ViewModel", StringComparison.Ordinal))
            name = name[..^"ViewModel".Length];
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private static void CenterRelativeTo(Window window, Window owner)
    {
        double scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1.0;
        double ownerW = owner.Bounds.Width > 0 ? owner.Bounds.Width : owner.Width;
        double ownerH = owner.Bounds.Height > 0 ? owner.Bounds.Height : owner.Height;
        double winW = window.Bounds.Width > 0 ? window.Bounds.Width : (window.Width > 0 ? window.Width : 600);
        double winH = window.Bounds.Height > 0 ? window.Bounds.Height : (window.Height > 0 ? window.Height : 450);

        if (double.IsNaN(ownerW) || double.IsNaN(ownerH) || ownerW <= 0 || ownerH <= 0)
            return;

        int x = owner.Position.X + (int)Math.Round((ownerW - winW) * scale / 2.0);
        int y = owner.Position.Y + (int)Math.Round((ownerH - winH) * scale / 2.0);
        window.Position = new PixelPoint(x, y);
    }
}
