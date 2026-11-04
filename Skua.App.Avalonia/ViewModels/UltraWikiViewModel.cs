using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Skua.App.Avalonia.ViewModels;

/// <summary>
/// Ultra boss guide built from the community Ultras spreadsheet (UltraWiki.json): teams, enhancements,
/// class ratings per role and a team builder that rates a team from 0 to 6 stars.
/// </summary>
public partial class UltraWikiViewModel : BotControlViewModelBase
{
    public const double MaxScore = 6;
    private const double BestGroupStars = 5;
    private const string HealerCategory = "Healer";
    private const string SupportCategory = "Support";
    private static readonly string[] DpsCategories = { "Endgame DPS", "Chrono DPS", "Early DPS" };
    private static readonly string[] CategoryOrder = { "Support", "Healer", "Endgame DPS", "Chrono DPS", "Early DPS" };

    public UltraWikiViewModel()
        : base("Ultra Wiki", 1120, 760)
    {
        Ultras = LoadUltras();
        ApplyFilter();
        SelectedUltra = FilteredUltras.FirstOrDefault();
    }

    public string Source { get; private set; } = string.Empty;

    public List<UltraEntry> Ultras { get; }

    public ObservableCollection<UltraEntry> FilteredUltras { get; } = new();

    public ObservableCollection<RatingColumn> RatingColumns { get; } = new();

    public ObservableCollection<TeamSlot> TeamSlots { get; } = new();

    public ObservableCollection<SlotResult> ScoreBreakdown { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUltra))]
    private UltraEntry? _selectedUltra;

    [ObservableProperty]
    private double _teamScore;

    [ObservableProperty]
    private string _teamStars = string.Empty;

    [ObservableProperty]
    private string _scoreSummary = string.Empty;

    public bool HasUltra => SelectedUltra != null;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedUltraChanged(UltraEntry? value)
    {
        RatingColumns.Clear();
        TeamSlots.Clear();
        if (value == null)
        {
            Recalculate();
            return;
        }

        foreach (string category in value.Ratings.Keys.OrderBy(k => Array.IndexOf(CategoryOrder, k) is int i && i >= 0 ? i : 99))
        {
            RatingColumns.Add(new RatingColumn(category, value.Ratings[category]
                .OrderByDescending(r => r.Stars)
                .Select(r => new RatingRow(r.Cls, r.Stars))
                .ToList()));
        }

        List<string> options = new() { string.Empty };
        options.AddRange(value.AllClasses);
        for (int i = 0; i < value.TeamSize; i++)
        {
            TeamSlot slot = new(i + 1, options);
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TeamSlot.SelectedClass))
                    Recalculate();
            };
            TeamSlots.Add(slot);
        }

        FillTeam(ResolveRecommended(value));
    }

    private void ApplyFilter()
    {
        string search = SearchText.Trim();
        FilteredUltras.Clear();
        foreach (UltraEntry ultra in Ultras.Where(u => search.Length == 0
                     || u.Boss.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || u.Tab.Contains(search, StringComparison.OrdinalIgnoreCase)))
            FilteredUltras.Add(ultra);
    }

    [RelayCommand]
    private void UseRecommended()
    {
        if (SelectedUltra != null)
            FillTeam(ResolveRecommended(SelectedUltra));
    }

    [RelayCommand]
    private void SuggestBest()
    {
        if (SelectedUltra != null)
            FillTeam(FindBestTeam(SelectedUltra));
    }

    [RelayCommand]
    private void ClearTeam() => FillTeam(Enumerable.Empty<string>());

    private bool _filling;

    private void FillTeam(IEnumerable<string> classes)
    {
        _filling = true;
        List<string> list = classes.ToList();
        for (int i = 0; i < TeamSlots.Count; i++)
            TeamSlots[i].SelectedClass = i < list.Count ? list[i] : string.Empty;
        _filling = false;
        Recalculate();
    }

    private void Recalculate()
    {
        if (_filling)
            return;

        ScoreBreakdown.Clear();
        if (SelectedUltra == null)
        {
            TeamScore = 0;
            TeamStars = string.Empty;
            ScoreSummary = string.Empty;
            return;
        }

        TeamResult result = Evaluate(SelectedUltra, TeamSlots.Select(s => s.SelectedClass).ToList());
        foreach (SlotResult slot in result.Slots)
            ScoreBreakdown.Add(slot);

        TeamScore = Math.Round(result.Total, 1);
        TeamStars = StarText(result.Total, MaxScore);
        ScoreSummary = $"Roles {result.Base:0.0}/5  +  recommended classes {result.Bonus:0.0}/1"
            + (result.Filled < SelectedUltra.TeamSize ? $"   ({SelectedUltra.TeamSize - result.Filled} empty slot(s) count as 0)" : string.Empty);
    }

    /// <summary>
    /// Rates a team: every member gets the stars of the role it fills (one Healer, one Support, the rest
    /// whatever they rate best at, picking the best assignment), averaged over the team size (0-5).
    /// Best Group classes count 5 stars in any slot.
    /// Repeated classes count half. Up to +1 for the named classes of the Recommended Group in the team.
    /// </summary>
    public static TeamResult Evaluate(UltraEntry ultra, IList<string?> team)
    {
        List<(int Slot, string Cls)> members = team
            .Select((c, i) => (Slot: i + 1, Cls: c?.Trim() ?? string.Empty))
            .Where(m => m.Cls.Length > 0)
            .ToList();

        double[] factor = new double[members.Count];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < members.Count; i++)
            factor[i] = seen.Add(members[i].Cls) ? 1 : 0.5;

        double bestTotal = -1;
        double bestRaw = -1;
        string[] bestRoles = new string[members.Count];
        double[] bestStars = new double[members.Count];

        // h/s = index of the Healer/Support member, -1 when nobody takes that role.
        for (int h = -1; h < members.Count; h++)
        {
            for (int s = -1; s < members.Count; s++)
            {
                if (s >= 0 && s == h)
                    continue;
                // A team of 2+ fills both roles; a single member just plays what it rates best at.
                if (members.Count >= 2 ? h < 0 || s < 0 : h >= 0 || s >= 0)
                    continue;

                double total = 0;
                double raw = 0;
                string[] roles = new string[members.Count];
                double[] stars = new double[members.Count];
                for (int i = 0; i < members.Count; i++)
                {
                    (string role, double value) = i == h
                        ? (HealerCategory, ultra.Rating(members[i].Cls, HealerCategory))
                        : i == s
                            ? (SupportCategory, ultra.Rating(members[i].Cls, SupportCategory))
                            : ultra.BestDps(members[i].Cls, DpsCategories, members.Count < 2);
                    // The sheet's own Best Group picks rate full stars in whatever slot they take,
                    // even where a single category under-rates them (e.g. StoneCrusher on Speaker).
                    raw += value;
                    if (value < BestGroupStars && ultra.InBestGroup(members[i].Cls))
                        (role, value) = ($"{role} · Best Group", BestGroupStars);
                    roles[i] = role;
                    stars[i] = value * factor[i];
                    total += stars[i];
                }

                // Ties go to the assignment the sheet rates higher, so roles read naturally.
                if (total > bestTotal || (total == bestTotal && raw > bestRaw))
                {
                    bestTotal = total;
                    bestRaw = raw;
                    bestRoles = roles;
                    bestStars = stars;
                }
            }
        }

        List<SlotResult> slots = new();
        for (int i = 0; i < members.Count; i++)
        {
            string note = factor[i] < 1 ? "repeated class, counts half" : ultra.HasAnyRating(members[i].Cls) ? string.Empty : "not rated for this ultra";
            slots.Add(new SlotResult(members[i].Slot, members[i].Cls, bestRoles[i], bestStars[i], note));
        }

        double baseScore = members.Count == 0 ? 0 : Math.Max(0, bestTotal) / ultra.TeamSize;
        List<string> named = ultra.Recommended.Where(ultra.IsClass).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        double bonus = named.Count == 0 ? 0 : (double)named.Count(n => members.Any(m => m.Cls.Equals(n, StringComparison.OrdinalIgnoreCase))) / named.Count;

        return new TeamResult(baseScore, bonus, Math.Min(MaxScore, baseScore + bonus), members.Count, slots);
    }

    /// <summary>
    /// The Recommended Group with its role placeholders ("Healer / Support", "DPS", ...) filled by the
    /// best rated class for those roles that is not in the team yet; Best Group classes win ties.
    /// </summary>
    public static List<string> ResolveRecommended(UltraEntry ultra)
    {
        List<string> team = ultra.Recommended.Where(ultra.IsClass).ToList();
        List<string> result = new();
        foreach (string entry in ultra.Recommended)
        {
            if (ultra.IsClass(entry))
            {
                result.Add(entry);
                continue;
            }

            List<string> categories = entry.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(role => role.Equals("DPS", StringComparison.OrdinalIgnoreCase)
                    ? DpsCategories
                    : new[] { role })
                .Where(ultra.Ratings.ContainsKey)
                .ToList();
            if (categories.Count == 0)
                continue;

            string? pick = ultra.AllClasses
                .Where(c => !team.Contains(c, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(c => categories.Max(cat => ultra.Rating(c, cat)))
                .ThenByDescending(ultra.InBestGroup)
                .FirstOrDefault();
            if (pick == null)
                continue;

            team.Add(pick);
            result.Add(pick);
        }

        return result;
    }

    /// <summary>Best rated team among the strongest classes of the ultra plus its recommended ones.</summary>
    public static List<string> FindBestTeam(UltraEntry ultra)
    {
        List<string> pool = ultra.AllClasses
            .OrderByDescending(c => ultra.BestOverall(c))
            .Take(14)
            .Union(ultra.Recommended.Where(ultra.IsClass), StringComparer.OrdinalIgnoreCase)
            .ToList();

        int size = Math.Min(ultra.TeamSize, pool.Count);
        List<string> best = new();
        double bestScore = -1;
        foreach (List<string> combo in Combinations(pool, size))
        {
            double score = Evaluate(ultra, combo.Cast<string?>().ToList()).Total;
            if (score > bestScore)
            {
                bestScore = score;
                best = combo;
            }
        }

        // Keep the recommended order first so the slots read like the sheet.
        return best
            .OrderBy(c => ultra.Recommended.FindIndex(r => r.Equals(c, StringComparison.OrdinalIgnoreCase)) is int i && i >= 0 ? i : 99)
            .ToList();
    }

    private static IEnumerable<List<string>> Combinations(List<string> items, int size)
    {
        int[] idx = Enumerable.Range(0, size).ToArray();
        if (size == 0 || size > items.Count)
            yield break;

        while (true)
        {
            yield return idx.Select(i => items[i]).ToList();
            int k = size - 1;
            while (k >= 0 && idx[k] == items.Count - size + k)
                k--;
            if (k < 0)
                yield break;
            idx[k]++;
            for (int j = k + 1; j < size; j++)
                idx[j] = idx[j - 1] + 1;
        }
    }

    public static string StarText(double value, double max)
    {
        int full = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        full = Math.Clamp(full, 0, (int)max);
        return new string('★', full) + new string('☆', (int)max - full);
    }

    private List<UltraEntry> LoadUltras()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UltraWiki.json");
            if (stream == null)
                return new();

            UltraWikiFile? file = JsonSerializer.Deserialize<UltraWikiFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Source = file?.Source ?? string.Empty;
            return file?.Ultras ?? new();
        }
        catch
        {
            return new();
        }
    }
}

public class UltraWikiFile
{
    public string Source { get; set; } = string.Empty;
    public List<UltraEntry> Ultras { get; set; } = new();
}

public class UltraEntry
{
    public string Tab { get; set; } = string.Empty;
    public string Boss { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<BestGroupEntry> BestGroup { get; set; } = new();
    public List<string> Recommended { get; set; } = new();
    public Dictionary<string, List<ClassRating>> Ratings { get; set; } = new();
    public string Mechanics { get; set; } = string.Empty;

    public string Title => Tab.Equals(Boss, StringComparison.OrdinalIgnoreCase) ? Boss : $"{Tab} · {Boss}";

    public string RecommendedText => string.Join("  ·  ", Recommended);

    public bool HasNote => Note.Length > 0;

    public bool HasMechanics => Mechanics.Length > 0;

    /// <summary>4-player ultras list 4 recommended members, 7-player ones list 7.</summary>
    public int TeamSize => Math.Max(Recommended.Count, BestGroup.Count) > 4 ? 7 : 4;

    private List<string>? _allClasses;

    public List<string> AllClasses => _allClasses ??= Ratings.Values.SelectMany(r => r)
        .Select(r => r.Cls)
        .Concat(BestGroup.Select(b => b.Cls))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool InBestGroup(string cls) => BestGroup.Any(b => b.Cls.Equals(cls, StringComparison.OrdinalIgnoreCase));

    public bool IsClass(string name) => AllClasses.Contains(name, StringComparer.OrdinalIgnoreCase);

    public double Rating(string cls, string category) =>
        Ratings.TryGetValue(category, out List<ClassRating>? list)
            ? list.Where(r => r.Cls.Equals(cls, StringComparison.OrdinalIgnoreCase)).Select(r => (double)r.Stars).DefaultIfEmpty(0).Max()
            : 0;

    /// <summary>Role a non Healer/Support member plays: its best DPS rating, or its best rating of any kind when <paramref name="anyRole"/>.</summary>
    public (string Role, double Stars) BestDps(string cls, string[] dpsCategories, bool anyRole)
    {
        IEnumerable<string> categories = anyRole ? Ratings.Keys : Ratings.Keys.Where(dpsCategories.Contains);
        (string Role, double Stars) best = ("DPS", 0);
        foreach (string category in categories)
        {
            double stars = Rating(cls, category);
            if (stars > best.Stars)
                best = (category, stars);
        }

        // A class only rated as Support/Healer can still fill a flex slot at that rating.
        if (best.Stars == 0 && !anyRole)
        {
            foreach (string category in Ratings.Keys)
            {
                double stars = Rating(cls, category);
                if (stars > best.Stars)
                    best = ($"{category} (flex)", stars);
            }
        }

        return best;
    }

    public double BestOverall(string cls) => Ratings.Keys.Select(k => Rating(cls, k)).DefaultIfEmpty(0).Max();

    public bool HasAnyRating(string cls) => Ratings.Values.Any(l => l.Any(r => r.Cls.Equals(cls, StringComparison.OrdinalIgnoreCase)));
}

public class BestGroupEntry
{
    public string Cls { get; set; } = string.Empty;
    public EnhancementSet? Enh { get; set; }

    public string EnhText => Enh == null ? "—" : $"{Enh.Weapon} | {Enh.Base}   ·   Helm {Enh.Helm}   ·   Cape {Enh.Cape}";
}

public class EnhancementSet
{
    public string Weapon { get; set; } = string.Empty;
    public string Base { get; set; } = string.Empty;
    public string Helm { get; set; } = string.Empty;
    public string Cape { get; set; } = string.Empty;
}

public class ClassRating
{
    public string Cls { get; set; } = string.Empty;
    public int Stars { get; set; }
}

public record RatingColumn(string Title, List<RatingRow> Rows);

public record RatingRow(string Cls, int Stars)
{
    public string StarText => UltraWikiViewModel.StarText(Stars, 5);
}

public record SlotResult(int Slot, string Cls, string Role, double Stars, string Note)
{
    public string StarText => UltraWikiViewModel.StarText(Stars, 5);
    public bool HasNote => Note.Length > 0;
}

public record TeamResult(double Base, double Bonus, double Total, int Filled, List<SlotResult> Slots);

public partial class TeamSlot : ObservableObject
{
    public TeamSlot(int number, List<string> options)
    {
        Number = number;
        Options = options;
    }

    public int Number { get; }

    public string Label => $"Player {Number}";

    public List<string> Options { get; }

    [ObservableProperty]
    private string? _selectedClass = string.Empty;
}
