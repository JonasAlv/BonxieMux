using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Avalonia.ViewModels.AppLogs;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.UserControls;

public partial class LogTabUserControl : UserControl
{
    private LogTabViewModel? _subscribed;

    public static readonly StyledProperty<bool> WrapLinesProperty =
        AvaloniaProperty.Register<LogTabUserControl, bool>(nameof(WrapLines));

    public static readonly StyledProperty<bool> ShowCopyHintProperty =
        AvaloniaProperty.Register<LogTabUserControl, bool>(nameof(ShowCopyHint), defaultValue: true);

    public bool WrapLines
    {
        get => GetValue(WrapLinesProperty);
        set => SetValue(WrapLinesProperty, value);
    }

    public bool ShowCopyHint
    {
        get => GetValue(ShowCopyHintProperty);
        set => SetValue(ShowCopyHintProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WrapLinesProperty)
            LogsListBox.Classes.Set("wrap", WrapLines);
        else if (change.Property == ShowCopyHintProperty)
            CopyHint.IsVisible = ShowCopyHint;
    }

    public LogTabUserControl()
    {
        InitializeComponent();
        this.AttachedToVisualTree += (_, _) => Subscribe(DataContext as LogTabViewModel);
        this.DetachedFromVisualTree += (_, _) => Subscribe(null);
        this.DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree())
                Subscribe(DataContext as LogTabViewModel);
        };
    }

    private void Subscribe(LogTabViewModel? vm)
    {
        if (ReferenceEquals(_subscribed, vm))
            return;

        if (_subscribed is not null)
            _subscribed.Logs.CollectionChanged -= Logs_CollectionChanged;

        _subscribed = vm;
        if (vm is null)
        {
            UpdateCountText();
            return;
        }

        vm.Logs.CollectionChanged += Logs_CollectionChanged;
        UpdateCountText();
        if (vm.Logs.Count > 0)
            Dispatcher.UIThread.Post(() => ScrollToEnd(vm), DispatcherPriority.Loaded);
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateCountText, DispatcherPriority.Background);
        LogTabViewModel? vm = _subscribed;
        if (vm is null || vm.Logs.Count == 0)
            return;

        if (LogsListBox.Scroll is ScrollViewer scroll && scroll.Extent.Height > scroll.Viewport.Height
            && scroll.Offset.Y + scroll.Viewport.Height < scroll.Extent.Height - 24)
            return;

        Dispatcher.UIThread.Post(() => ScrollToEnd(vm));
    }

    private void ScrollToEnd(LogTabViewModel vm)
    {
        if (ReferenceEquals(_subscribed, vm) && vm.Logs.Count > 0)
            LogsListBox.ScrollIntoView(vm.Logs.Count - 1);
    }

    private async void LogsListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C)
        {
            await CopySelectedLogsAsync();
            e.Handled = true;
        }
    }

    public async Task CopySelectedOrAllAsync()
    {
        if (LogsListBox.SelectedItems is { Count: > 0 })
            await CopySelectedLogsAsync();
        else if (DataContext is LogTabViewModel vm)
            vm.CopyLogCommand.Execute(null);
    }

    public void DeselectAll()
    {
        LogsListBox.SelectedItems?.Clear();
    }

    private void LogsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCountText();
    }

    private void JumpToBottom_Click(object? sender, RoutedEventArgs e)
    {
        if (_subscribed is { } vm)
            ScrollToEnd(vm);
    }

    private void UpdateCountText()
    {
        int shown = _subscribed?.Logs.Count ?? 0;
        int total = _subscribed?.TotalCount ?? shown;
        int selected = LogsListBox.SelectedItems?.Count ?? 0;
        string text = shown == total
            ? (shown == 1 ? "1 line" : $"{shown:N0} lines")
            : $"{shown:N0} / {total:N0} lines";
        CountText.Text = selected > 0 ? $"{text} · {selected} selected" : text;
    }

    private async void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        await CopySelectedLogsAsync();
    }

    private async Task CopySelectedLogsAsync()
    {
        if (LogsListBox.SelectedItems is { Count: > 0 } items)
        {
            string text = string.Join(Environment.NewLine, items.Cast<object>().Select(x => x.ToString()));
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                try
                {
                    await clipboard.SetTextAsync(text);
                    UpdateCountText();
                }
                catch (Exception)
                {
                    CountText.Text = "Couldn't copy: the clipboard is busy, try again";
                }
            }
        }
    }
}
