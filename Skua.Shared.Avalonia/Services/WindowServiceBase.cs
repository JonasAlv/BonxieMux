using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.Shared.Avalonia.Services;

public class WindowServiceBase : IWindowService
{
    protected readonly Dictionary<string, Window> _managedWindows = new(StringComparer.OrdinalIgnoreCase);
    protected readonly Dictionary<string, Func<IManagedWindow>> _managedWindowFactories = new(StringComparer.OrdinalIgnoreCase);

    public virtual void ShowWindow<TViewModel>(int width, int height) where TViewModel : class
    {
        TViewModel vm = Ioc.Default.GetRequiredService<TViewModel>();
        Window window = CreateHostWindow(vm, typeof(TViewModel).Name, width, height);
        ShowHostWindow(window);
    }

    public virtual void ShowWindow<TViewModel>() where TViewModel : class =>
        ShowWindow<TViewModel>(900, 600);

    public virtual void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        Window window = CreateHostWindow(viewModel, typeof(TViewModel).Name, 900, 600);
        ShowHostWindow(window);
    }

    public void ShowManagedWindow(string key)
    {
        if (!_managedWindows.TryGetValue(key, out Window? window))
        {
            if (!_managedWindowFactories.TryGetValue(key, out var factory))
                return;

            var vm = factory();
            window = CreateHostWindow(vm, vm.Title, vm.Width, vm.Height);
            window.CanResize = vm.CanResize;
            AttachManagedClosing(window);
            _managedWindows[key] = window;
        }

        if (!window.IsVisible)
            ShowHostWindow(window);
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Focus();

        if (window.DataContext is ObservableRecipient r)
            r.IsActive = true;
    }

    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow
    {
        if (_managedWindows.ContainsKey(key))
            return;

        Window window = CreateHostWindow(viewModel, viewModel.Title, viewModel.Width, viewModel.Height);
        window.CanResize = viewModel.CanResize;
        AttachManagedClosing(window);
        _managedWindows[key] = window;
    }

    public void RegisterManagedWindow<TViewModel>(string key) where TViewModel : class, IManagedWindow
    {
        _managedWindowFactories[key] = () => Ioc.Default.GetRequiredService<TViewModel>();
    }

    public void RegisterManagedWindow(string key, Func<IManagedWindow> factory)
    {
        _managedWindowFactories[key] = factory;
    }

    protected virtual void ShowHostWindow(Window window)
    {
        window.Show();
    }

    private static void AttachManagedClosing(Window window)
    {
        window.Closing += (_, e) =>
        {
            e.Cancel = true;
            window.Hide();
            if (window.DataContext is ObservableRecipient recipient)
                recipient.IsActive = false;
        };
    }

    protected static Window CreateHostWindow(object viewModel, string title, double width, double height)
    {
        bool hasTemplate = Application.Current?.DataTemplates.Any(t => t.Match(viewModel)) == true;
        SizeToContent sizeToContent = (width <= 0, height <= 0) switch
        {
            (true, true) => SizeToContent.WidthAndHeight,
            (true, false) => SizeToContent.Width,
            (false, true) => SizeToContent.Height,
            _ => SizeToContent.Manual
        };

        PixelPoint? initialPos = viewModel is IManagedWindow managed && managed.X.HasValue && managed.Y.HasValue
            ? new PixelPoint(managed.X.Value, managed.Y.Value)
            : null;

        Window window = new()
        {
            Title = title,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            SizeToContent = sizeToContent,
            DataContext = viewModel,
            Content = ShellWindowHelper.CreateShellContent(
                title,
                hasTemplate
                    ? new ContentControl
                    {
                        Content = viewModel,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Stretch
                    }
                    : CreateFallbackContent(title))
        };
        ShellWindowHelper.ConfigureFramelessWindow(window);

        if (width > 0)
        {
            window.Width = width;
            window.MinWidth = Math.Min(width, 400);
        }
        if (height > 0)
        {
            window.Height = height;
            window.MinHeight = Math.Min(height, 300);
        }

        if (initialPos.HasValue)
        {
            window.Position = initialPos.Value;
            window.Opened += (_, _) => window.Position = initialPos.Value;
        }
        else
        {
            WindowPlacementHelper.CenterOnActiveScreen(window);
        }

        Style popupStyle = new(x => x.OfType<Popup>());
        popupStyle.Setters.Add(new Setter(Popup.ShouldUseOverlayLayerProperty, true));
        window.Styles.Add(popupStyle);

        return window;
    }

    private static Control CreateFallbackContent(string title) =>
        new Border
        {
            Padding = new Thickness(16),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left
            }
        };
}
