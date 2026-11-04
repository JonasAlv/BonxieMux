using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;
using Skua.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.Options;

/// <summary>
/// The options the Game Options window shows and changes: this client's own, or those of another open account.
/// Everything changed through the window counts as unsaved for that account until Save (or Reset / Defaults).
/// </summary>
public abstract class OptionSource : ObservableObject, IOptionDictionary, IDisposable
{
    public abstract string Account { get; }
    public abstract bool IsLocal { get; }
    public abstract ImmutableDictionary<string, Func<object>> OptionDictionary { get; }

    /// <summary>The bool, int and string options with their types.</summary>
    public abstract IReadOnlyList<(string Name, Type Type)> Properties { get; }

    /// <summary>Changed in this window and not saved yet.</summary>
    public bool HasUnsavedChanges { get; protected set; }

    public object? Get(string name) => OptionDictionary.TryGetValue(name, out Func<object>? read) ? read() : null;

    public abstract void Set(string name, object? value);
    public abstract void Save();
    public abstract void Reset();
    public abstract void ResetToDefault();

    public virtual void Dispose() { }

    protected static object? Convert(object? value, Type type)
    {
        if (value is null)
            return type == typeof(string) ? string.Empty : null;
        if (type == typeof(bool))
            return value is bool b ? b : bool.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!);
        if (type == typeof(int))
            return value is int i ? i : int.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        return System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>This client's own options (changes apply now, as before).</summary>
public sealed class LocalOptionSource : OptionSource
{
    private readonly IScriptOption _options;
    private readonly List<(string, Type)> _properties;

    public LocalOptionSource(IScriptOption options)
    {
        _options = options;
        _properties = OptionSharing.SharedProperties(options).Select(p => (p.Name, p.PropertyType)).ToList();
        if (options is INotifyPropertyChanged notify)
            notify.PropertyChanged += Forward;
    }

    private void Forward(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(e.PropertyName);

    public override string Account => "This client";
    public override bool IsLocal => true;
    public override ImmutableDictionary<string, Func<object>> OptionDictionary => ((IOptionDictionary)_options).OptionDictionary;
    public override IReadOnlyList<(string Name, Type Type)> Properties => _properties;

    public override void Set(string name, object? value)
    {
        var property = OptionSharing.SharedProperties(_options).FirstOrDefault(p => p.Name == name);
        if (property is null)
            return;
        object? converted = Convert(value, property.PropertyType);
        if (Equals(property.GetValue(_options), converted))
            return;
        property.SetValue(_options, converted);
        HasUnsavedChanges = true;
    }

    public override void Save()
    {
        _options.Save();
        HasUnsavedChanges = false;
    }

    public override void Reset()
    {
        _options.Reset();
        HasUnsavedChanges = false;
    }

    public override void ResetToDefault()
    {
        _options.ResetToDefault();
        HasUnsavedChanges = true;
    }

    public override void Dispose()
    {
        if (_options is INotifyPropertyChanged notify)
            notify.PropertyChanged -= Forward;
    }
}

/// <summary>
/// Another open account: its values come from the file it publishes and every change is sent to it as a command, so it
/// applies the change live and, on Save, keeps it.
/// </summary>
public sealed class RemoteOptionSource : OptionSource
{
    private readonly int _processId;
    private readonly string _account;
    private readonly Dictionary<string, object?> _values = new();
    private readonly List<(string, Type)> _properties = new();
    private readonly Dictionary<string, DateTime> _editedAt = new();
    private ImmutableDictionary<string, Func<object>> _dictionary = ImmutableDictionary<string, Func<object>>.Empty;
    private DateTime _lastRead = DateTime.MinValue;

    private RemoteOptionSource(int processId, string account)
    {
        _processId = processId;
        _account = account;
    }

    /// <summary>The account's options, or null when it has not reported them.</summary>
    public static RemoteOptionSource? TryOpen(int processId, string account)
    {
        SharedOptions? shared = OptionSharing.Read(processId);
        if (shared is null)
            return null;
        var source = new RemoteOptionSource(processId, string.IsNullOrWhiteSpace(shared.Account) ? account : shared.Account);
        foreach (var (name, value) in shared.Values)
        {
            Type type = value.Kind == "b" ? typeof(bool) : value.Kind == "i" ? typeof(int) : typeof(string);
            source._properties.Add((name, type));
            source._values[name] = Convert(value.Value, type);
        }
        source._dictionary = source._values.Keys.ToImmutableDictionary(k => k, k => (Func<object>)(() => source._values[k] ?? string.Empty));
        source._lastRead = shared.UpdatedUtc;
        return source;
    }

    public override string Account => _account;
    public override bool IsLocal => false;
    public override ImmutableDictionary<string, Func<object>> OptionDictionary => _dictionary;
    public override IReadOnlyList<(string Name, Type Type)> Properties => _properties;

    /// <summary>True while the account is still reporting.</summary>
    public bool IsAlive => OptionSharing.Read(_processId) is not null;

    /// <summary>Picks up changes made on the account itself (not the options just edited here, until the account has them).</summary>
    public void Refresh()
    {
        SharedOptions? shared = OptionSharing.Read(_processId);
        if (shared is null || shared.UpdatedUtc <= _lastRead)
            return;
        _lastRead = shared.UpdatedUtc;
        foreach (var (name, value) in shared.Values)
        {
            if (!_values.TryGetValue(name, out object? current))
                continue;
            if (_editedAt.TryGetValue(name, out DateTime edited) && shared.UpdatedUtc <= edited.AddSeconds(1))
                continue;
            Type type = _properties.First(p => p.Item1 == name).Item2;
            object? fresh = Convert(value.Value, type);
            if (!Equals(current, fresh))
            {
                _values[name] = fresh;
                OnPropertyChanged(name);
            }
        }
    }

    public override void Set(string name, object? value)
    {
        if (!_values.TryGetValue(name, out object? current))
            return;
        Type type = _properties.First(p => p.Item1 == name).Item2;
        object? converted = Convert(value, type);
        if (Equals(current, converted))
            return;
        _values[name] = converted;
        _editedAt[name] = DateTime.UtcNow;
        HasUnsavedChanges = true;
        OptionSharing.Send(_processId, new SharedOptionCommand("set", name, System.Convert.ToString(converted, CultureInfo.InvariantCulture)));
        OnPropertyChanged(name);
    }

    public override void Save()
    {
        OptionSharing.Send(_processId, new SharedOptionCommand("save"));
        HasUnsavedChanges = false;
    }

    public override void Reset()
    {
        OptionSharing.Send(_processId, new SharedOptionCommand("reset"));
        _editedAt.Clear();
        HasUnsavedChanges = false;
    }

    public override void ResetToDefault()
    {
        OptionSharing.Send(_processId, new SharedOptionCommand("defaults"));
        _editedAt.Clear();
        HasUnsavedChanges = true;
    }
}
