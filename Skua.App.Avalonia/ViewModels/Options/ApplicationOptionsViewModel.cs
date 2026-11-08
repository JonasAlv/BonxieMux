using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.Options;

public class OptionGroupViewModel
{
    public string Title { get; set; } = string.Empty;
    public string ShortTitle { get; set; } = string.Empty;
    public string Icon { get; set; } = "Cog";
    public string Description { get; set; } = string.Empty;
    public List<DisplayOptionItemViewModelBase> Items { get; set; } = new();
    public bool IsAll { get; private init; }
    public int Count => Items.Count;

    public static OptionGroupViewModel CreateAll(IEnumerable<OptionGroupViewModel> groups) => new()
    {
        Title = "All",
        ShortTitle = "All",
        Icon = "Apps",
        Items = groups.SelectMany(g => g.Items).ToList(),
        IsAll = true
    };

    public static IEnumerable<OptionGroupViewModel> Filter(IEnumerable<OptionGroupViewModel> groups, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return groups;

        string text = search.Trim();
        return groups.Select(g => new OptionGroupViewModel
        {
            Title = g.Title,
            ShortTitle = g.ShortTitle,
            Icon = g.Icon,
            Description = g.Description,
            Items = g.Items.Where(i => i.Content.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                       (i.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)).ToList()
        }).Where(g => g.Items.Count > 0);
    }
}

public partial class ApplicationOptionsViewModel : BotControlViewModelBase
{
    private static readonly string[] GeneralKeys = { "ShowUsernameInTitle", "SaveLogsToFile", "ClientAnim", "ReloginTryDelay", "ReloginWatchdog", "ClearFlashCache", "GraphicsApi" };
    private static readonly string[] UpdateKeys =
    {
        "CheckBotScriptsUpdates", "AutoUpdateBotScripts", "CheckAdvanceSkillSetsUpdates", "AutoUpdateAdvanceSkillSetsUpdates",
        "CheckJunkItemsUpdates", "AutoUpdateJunkItems"
    };

    private string _searchText = string.Empty;
    private OptionGroupViewModel _selectedSection;

    public ApplicationOptionsViewModel(List<DisplayOptionItemViewModelBase> appOptions)
        : base("Application Options", 580, 650)
    {
        ApplicationOptions = appOptions;
        BuildGroups();
        Sections = new List<OptionGroupViewModel> { OptionGroupViewModel.CreateAll(OptionGroups) }.Concat(OptionGroups).ToList();
        _selectedSection = Sections[0];
    }

    public List<DisplayOptionItemViewModelBase> ApplicationOptions { get; }
    public List<OptionGroupViewModel> OptionGroups { get; private set; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                OnPropertyChanged(nameof(FilteredGroups));
        }
    }

    public List<OptionGroupViewModel> Sections { get; }

    public OptionGroupViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is not null && SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(FilteredGroups));
                OnPropertyChanged(nameof(ShowSectionHeaders));
            }
        }
    }

    public bool ShowSectionHeaders => _selectedSection.IsAll;

    public IEnumerable<OptionGroupViewModel> FilteredGroups =>
        OptionGroupViewModel.Filter(_selectedSection.IsAll ? OptionGroups : new[] { _selectedSection }, _searchText);

    private void BuildGroups()
    {
        List<DisplayOptionItemViewModelBase> InSection(string[] keys) =>
            keys.Select(k => ApplicationOptions.FirstOrDefault(i => i.Tag == k)).OfType<DisplayOptionItemViewModelBase>().ToList();
        List<DisplayOptionItemViewModelBase> updateItems = InSection(UpdateKeys);
        List<DisplayOptionItemViewModelBase> generalItems = ApplicationOptions.Except(updateItems).ToList();

        OptionGroups = new List<OptionGroupViewModel>
        {
            new()
            {
                Title = "General & Performance",
                Icon = "ApplicationCogOutline",
                Description = "Client interface, animation frame rates, relogin timings, and cache",
                ShortTitle = "General",
                Items = generalItems
            },
            new()
            {
                Title = "Auto Updates & Sync",
                Icon = "CloudSyncOutline",
                Description = "Automatic synchronization for bot scripts, skill sets, and item database",
                ShortTitle = "Updates",
                Items = updateItems
            }
        };
    }
}
