using Avalonia.Controls;
using Avalonia.Input;
using Skua.Shared.Avalonia.Services;

namespace Skua.Shared.Avalonia.Controls.Shell;

public sealed record QuickPickItem(string Category, string Title, string? Detail, Action OnSelect);

public partial class QuickPickWindow : Window
{
    private static QuickPickWindow? _active;
    private bool _closing;
    private readonly List<QuickPickItem> _items;

    public QuickPickWindow()
    {
        InitializeComponent();
        _items = [];
    }

    public QuickPickWindow(IEnumerable<QuickPickItem> items, string title = "Quick Pick", string placeholder = "Type to filter…")
    {
        InitializeComponent();
        _items = items.ToList();
        Title = title;
        SearchBox.PlaceholderText = placeholder;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SafeClose();
                e.Handled = true;
            }
        };
        Deactivated += (_, _) => SafeClose();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_active, this))
                _active = null;
        };
        Opened += (_, _) =>
        {
            Refresh();
            SearchBox.Focus();
        };
    }

    public static bool DismissActive()
    {
        if (_active is null)
            return false;
        _active.SafeClose();
        return true;
    }

    public void ShowModeless(Window? owner)
    {
        _active?.SafeClose();
        _active = this;
        if (owner is { IsVisible: true })
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Show(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowPlacementHelper.CenterOnActiveScreen(this);
            Show();
            Activate();
        }
    }

    private void SafeClose()
    {
        if (_closing)
            return;
        _closing = true;
        if (ReferenceEquals(_active, this))
            _active = null;
        try { Close(); } catch { }
    }

    private void Refresh()
    {
        string query = (SearchBox.Text ?? string.Empty).Trim();
        IEnumerable<QuickPickItem> matches = _items;
        if (query.Length > 0)
        {
            string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            matches = _items
                .Where(e => words.All(w =>
                    e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || e.Category.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || (e.Detail?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
                .OrderBy(e => e.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);
        }

        ItemsList.ItemsSource = matches.ToList();
        if (ItemsList.ItemCount > 0)
            ItemsList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private void ItemsList_Tapped(object? sender, TappedEventArgs e) => RunSelected();

    private void ItemsList_DoubleTapped(object? sender, TappedEventArgs e) => RunSelected();

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when ItemsList.ItemCount > 0:
                ItemsList.SelectedIndex = Math.Min(ItemsList.SelectedIndex + 1, ItemsList.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up when ItemsList.ItemCount > 0:
                ItemsList.SelectedIndex = Math.Max(ItemsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void RunSelected()
    {
        if (ItemsList.SelectedItem is not QuickPickItem item)
            return;
        SafeClose();
        try { item.OnSelect(); } catch { }
    }
}
