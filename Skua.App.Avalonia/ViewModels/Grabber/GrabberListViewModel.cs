using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.ViewModels.Grabber;

public partial class GrabberListViewModel : ObservableRecipient
{
    public GrabberListViewModel(
        string title, 
        IGrabberService grabberService, 
        GrabberTypes grabType, 
        IEnumerable<GrabberTaskViewModel> commands, 
        bool selectMultiple = false,
        IClipboardService? clipboardService = null,
        IFileDialogService? fileDialogService = null)
    {
        Title = title;
        SelectMultiple = selectMultiple;
        _grabberService = grabberService;
        _grabType = grabType;
        _grabberCommands = new(commands);
        _clipboardService = clipboardService ?? Ioc.Default.GetRequiredService<IClipboardService>();
        _fileDialogService = fileDialogService ?? Ioc.Default.GetRequiredService<IFileDialogService>();
    }

    public GrabberListViewModel(
        string title, 
        IGrabberService grabberService, 
        GrabberTypes grabType, 
        GrabberTaskViewModel command, 
        bool selectMultiple = false,
        IClipboardService? clipboardService = null,
        IFileDialogService? fileDialogService = null)
        : this(title, grabberService, grabType, new[] { command }, selectMultiple, clipboardService, fileDialogService)
    {
    }

    public GrabberListViewModel(
        string title, 
        IGrabberService grabberService, 
        GrabberTypes grabType, 
        bool selectMultiple = false,
        IClipboardService? clipboardService = null,
        IFileDialogService? fileDialogService = null)
        : this(title, grabberService, grabType, Enumerable.Empty<GrabberTaskViewModel>(), selectMultiple, clipboardService, fileDialogService)
    {
    }

    private readonly GrabberTypes _grabType;
    private readonly IGrabberService _grabberService;
    private readonly IClipboardService _clipboardService;
    private readonly IFileDialogService _fileDialogService;
    private readonly List<object> _allItems = new();

    public string Title { get; }
    public bool SelectMultiple { get; }

    // Sidebar grouping and icon for the Grabber window; set where the tabs are created.
    public string Group { get; init; } = string.Empty;
    public Material.Icons.MaterialIconKind Icon { get; init; }
    public bool IsSelectable => true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy = false;

    [ObservableProperty]
    private bool _showMessage = false;

    [ObservableProperty]
    private string _progressReportMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<GrabberTaskViewModel> _grabberCommands;

    [ObservableProperty]
    private bool _isMultipleSelection = true;

    [ObservableProperty]
    private RangedObservableCollection<object> _grabbedItems = new();

    [ObservableProperty]
    private object? _selectedItem;

    [RelayCommand]
    private async Task Grab()
    {
        IsBusy = true;
        ProgressReportMessage = "Working...";
        ShowMessage = true;
        bool succeeded = false;
        try
        {
            IEnumerable<object> items = await Task.Run(() => _grabberService.Grab(_grabType));
            _allItems.Clear();
            _allItems.AddRange(items);
            ApplyFilter();
            succeeded = true;
        }
        catch (Exception ex)
        {
            ProgressReportMessage = $"Grab failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (succeeded)
                ProgressReportMessage = "Finished.";

            await Task.Delay(succeeded ? 1000 : 4000);
            ShowMessage = false;
            ProgressReportMessage = string.Empty;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            GrabbedItems.ReplaceRange(_allItems);
        }
        else
        {
            string query = SearchText.Trim();
            var filtered = _allItems.Where(item => FormatGrabbedItem(item).Contains(query, StringComparison.OrdinalIgnoreCase));
            GrabbedItems.ReplaceRange(filtered);
        }
    }

    public void Copy(IList<object>? selectedItems = null)
    {
        var targetItems = (selectedItems != null && selectedItems.Count > 0)
            ? selectedItems
            : GrabbedItems;

        if (targetItems.Count == 0)
        {
            ShowTemporaryMessage("No items to copy.");
            return;
        }

        string text = BuildExportText(targetItems);
        _clipboardService.SetText(text);
        ShowTemporaryMessage($"Copied {targetItems.Count} item(s) to clipboard.");
    }

    public void ExportToFile(IList<object>? selectedItems = null)
    {
        var targetItems = (selectedItems != null && selectedItems.Count > 0)
            ? selectedItems
            : GrabbedItems;

        if (targetItems.Count == 0)
        {
            ShowTemporaryMessage("No items to export.");
            return;
        }

        string text = BuildDetailedExportText(targetItems);
        _fileDialogService.SaveText(text);
        ShowTemporaryMessage($"Exported {targetItems.Count} item(s) to file.");
    }

    private void ShowTemporaryMessage(string message)
    {
        ProgressReportMessage = message;
        ShowMessage = true;
        Task.Run(async () =>
        {
            await Task.Delay(2500);
            Dispatcher.UIThread.Post(() =>
            {
                if (ProgressReportMessage == message)
                {
                    ShowMessage = false;
                    ProgressReportMessage = string.Empty;
                }
            });
        });
    }

    public static string FormatGrabbedItem(object item)
    {
        return item switch
        {
            Quest q => $"[{q.ID}] {q.Name}" + (q.Slot > 0 ? $" (Slot: {q.Slot})" : "") + (q.Upgrade ? " [Member]" : ""),
            InventoryItem inv => $"[{inv.ID}] {inv.Name} x{inv.Quantity} | Category: {inv.Category}" + (inv.Coins ? " [AC]" : "") + (inv.Upgrade ? " [Member]" : ""),
            ShopItem s => $"[{s.ID}] {s.Name} | Cost: {s.Cost:N0} {(s.Coins ? "AC" : "Gold")}" + (s.Upgrade ? " [Member]" : "") + (s.Requirements?.Count > 0 ? $" Requirements: {string.Join(", ", s.Requirements.Select(r => $"[{r.ID}] {r.Name} x{r.Quantity}"))}" : ""),
            Monster m => $"[{m.ID}] {m.Name} | Cell: {m.Cell} | HP: {m.HP}/{m.MaxHP}",
            MapItem mi => $"[{mi.ID}] Map Item (Quest: {mi.QuestID})",
            ShopInfo info => $"[{info.ID}] {info.Name}",
            ItemBase i => $"[{i.ID}] {i.Name} x{i.Quantity}" + (i.Coins ? " [AC]" : "") + (i.Upgrade ? " [Member]" : ""),
            _ => item.ToString() ?? string.Empty
        };
    }

    public string BuildExportText(IEnumerable<object> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        sb.AppendLine($"=== {Title} ({list.Count} items) ===");
        foreach (var item in list)
        {
            sb.AppendLine(FormatGrabbedItem(item));
        }
        return sb.ToString();
    }

    // Export writes everything the grab returned: a block per item with every public property, and nested lists
    // (quest requirements, rewards, ...) expanded underneath. Copy keeps the one-line summary from BuildExportText.
    public string BuildDetailedExportText(IEnumerable<object> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        sb.AppendLine($"=== {Title} ({list.Count} items) ===");
        foreach (var item in list)
        {
            sb.AppendLine();
            sb.AppendLine(FormatGrabbedItem(item));
            AppendProperties(sb, item, 1, new HashSet<object>(ReferenceEqualityComparer.Instance) { item });
        }
        return sb.ToString();
    }

    private const int MaxExportDepth = 4;

    private static void AppendProperties(StringBuilder sb, object obj, int depth, HashSet<object> visited)
    {
        string indent = new(' ', depth * 2);
        foreach (var property in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                sb.AppendLine($"{indent}{property.Name}: (none)");
            }
            else if (IsSimpleValue(value))
            {
                sb.AppendLine($"{indent}{property.Name}: {value}");
            }
            else if (value is System.Collections.IEnumerable enumerable)
            {
                List<object> entries;
                try
                {
                    entries = enumerable.Cast<object?>().Where(e => e is not null).Cast<object>().ToList();
                }
                catch
                {
                    // A collection that throws while iterated is skipped, not allowed to abort the whole export.
                    sb.AppendLine($"{indent}{property.Name}: (unreadable)");
                    continue;
                }

                sb.AppendLine($"{indent}{property.Name} ({entries.Count}):");
                foreach (var entry in entries)
                {
                    if (IsSimpleValue(entry))
                    {
                        sb.AppendLine($"{indent}  - {entry}");
                        continue;
                    }

                    sb.AppendLine($"{indent}  - {FormatGrabbedItem(entry)}");
                    if (depth < MaxExportDepth && visited.Add(entry))
                        AppendProperties(sb, entry, depth + 2, visited);
                }
            }
            else if (depth < MaxExportDepth && visited.Add(value))
            {
                sb.AppendLine($"{indent}{property.Name}:");
                AppendProperties(sb, value, depth + 1, visited);
            }
            else
            {
                sb.AppendLine($"{indent}{property.Name}: {value}");
            }
        }
    }

    private static bool IsSimpleValue(object value)
    {
        Type type = value.GetType();
        return type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid;
    }

    [RelayCommand]
    private void CancelTask()
    {
        WeakReferenceMessenger.Default.Send<CancelGrabberTaskMessage>();
    }

    protected override void OnActivated()
    {
        foreach (GrabberTaskViewModel task in GrabberCommands)
            task.IsActive = true;

        Messenger.Register<GrabberListViewModel, PropertyChangedMessage<bool>>(this, IsBusyChanged);
        Messenger.Register<GrabberListViewModel, PropertyChangedMessage<string>>(this, ProgressReportMessageChanged);
    }

    protected override void OnDeactivated()
    {
        foreach (GrabberTaskViewModel task in GrabberCommands)
            task.IsActive = false;

        base.OnDeactivated();
    }

    public void IsBusyChanged(GrabberListViewModel receiver, PropertyChangedMessage<bool> message)
    {
        if (message.Sender.GetType() == typeof(GrabberTaskViewModel)
            && message.PropertyName == nameof(GrabberTaskViewModel.IsBusy))
            receiver.IsBusy = message.NewValue;
    }

    public void ProgressReportMessageChanged(GrabberListViewModel receiver, PropertyChangedMessage<string> message)
    {
        if (message.Sender.GetType() == typeof(GrabberTaskViewModel)
            && message.PropertyName == nameof(GrabberTaskViewModel.ProgressReportMessage))
        {
            ShowMessage = !string.IsNullOrEmpty(message.NewValue);
            receiver.ProgressReportMessage = message.NewValue;
        }
    }
}