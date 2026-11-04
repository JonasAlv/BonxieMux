using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Skua.App.Avalonia.Services;

public class HotKeyService : IHotKeyService, IDisposable
{
    public HotKeyService(Dictionary<string, IRelayCommand> hotKeys, ISettingsService settingsService, IDecamelizer decamelizer)
    {
        _hotKeys = hotKeys;
        _settingsService = settingsService;
        _decamelizer = decamelizer;
    }

    private readonly Dictionary<string, IRelayCommand> _hotKeys;
    private readonly ISettingsService _settingsService;
    private readonly IDecamelizer _decamelizer;
    private readonly List<HotKeyBinding> _bindings = [];
    private readonly object _bindingsLock = new();
    private Window? _mainWindow;
    private volatile bool _isMainWindowActive;
    private volatile bool _disposed;
    private X11HotKeyGrabber? _x11Grabber;
    private bool _waitingForMainWindow;

    public void Reload()
    {
        if (_disposed)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reload, DispatcherPriority.Normal);
            return;
        }

        _settingsService.ReloadSettings();
        StringCollection hotkeys = _settingsService.Get<StringCollection>("HotKeys") ?? new StringCollection();
        int initialCount = hotkeys.Count;
        EnsureDefaultBindingsExist(hotkeys);
        if (hotkeys.Count != initialCount)
            _settingsService.Set("HotKeys", hotkeys);

        AttachMainWindow();
        List<HotKeyBinding> bindings = [];

        foreach (string hk in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hk))
                continue;

            string[] split = hk.Split('|');
            string binding = split[0];
            if (!_hotKeys.TryGetValue(binding, out IRelayCommand? command) &&
                !global::Skua.Core.AppStartup.HotKeys.GetRegistry().TryGetValue(binding, out command))
                continue;

            if (command is null)
                continue;

            if (split.Length < 2 || string.IsNullOrWhiteSpace(split[1]) || split[1].Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
                continue;

            KeyGestureBinding? parsed = ParseGesture(split[1]);
            if (parsed is null)
            {
                StrongReferenceMessenger.Default.Send<HotKeyErrorMessage>(new(binding));
                continue;
            }

            bindings.Add(new HotKeyBinding(binding, command, parsed.Key, parsed.Modifiers, TryToVirtualKey(parsed.Key)));
        }

        lock (_bindingsLock)
        {
            _bindings.Clear();
            _bindings.AddRange(bindings);
        }

        UpdateX11Grabs(bindings);
    }

    public List<T> GetHotKeys<T>()
        where T : IHotKey, new()
    {
        StringCollection hotkeys = _settingsService.Get<StringCollection>("HotKeys") ?? new StringCollection();
        EnsureDefaultBindingsExist(hotkeys);

        Dictionary<string, IRelayCommand> registeredActions = global::Skua.Core.AppStartup.HotKeys.GetRegistry();
        List<T> parsed = [];
        StringCollection cleaned = new();

        foreach (string hk in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hk))
                continue;

            string[] split = hk.Split('|');
            string binding = split[0];
            string gesture = split.Length > 1 ? split[1] : string.Empty;

            if (!_hotKeys.ContainsKey(binding) && !registeredActions.ContainsKey(binding))
                continue;

            if (!string.IsNullOrWhiteSpace(gesture) && !gesture.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                cleaned.Add($"{binding}|{gesture}");
                parsed.Add(new()
                {
                    Binding = binding,
                    Title = global::Skua.Core.AppStartup.HotKeys.GetFormattedTitle(binding),
                    KeyGesture = gesture
                });
            }
        }

        if (parsed.Count == 0)
        {
            EnsureDefaultBindingsExist(cleaned);
            foreach (string hk in cleaned)
            {
                string[] split = hk.Split('|');
                string binding = split[0];
                string gesture = split.Length > 1 ? split[1] : string.Empty;
                if (_hotKeys.ContainsKey(binding) || registeredActions.ContainsKey(binding))
                {
                    parsed.Add(new()
                    {
                        Binding = binding,
                        Title = global::Skua.Core.AppStartup.HotKeys.GetFormattedTitle(binding),
                        KeyGesture = gesture
                    });
                }
            }
        }

        _settingsService.Set("HotKeys", cleaned);
        return parsed;
    }

    public HotKey? ParseToHotKey(string keyGesture)
    {
        if (string.IsNullOrWhiteSpace(keyGesture))
            return null;

        KeyGestureBinding? parsed = ParseGesture(keyGesture);
        if (parsed is null)
            return null;

        string normalized = parsed.Key.ToString();
        return new HotKey(
            normalized,
            parsed.Modifiers.HasFlag(KeyModifiers.Control),
            parsed.Modifiers.HasFlag(KeyModifiers.Alt),
            parsed.Modifiers.HasFlag(KeyModifiers.Shift));
    }

    private void EnsureDefaultBindingsExist(StringCollection hotkeys)
    {
        bool HasBinding(string name)
        {
            foreach (string? item in hotkeys)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                string[] parts = item.Split('|');
                if (parts.Length > 0 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        if (hotkeys.Count == 0)
        {
            hotkeys.Add("ToggleScript|F5");
            hotkeys.Add("OpenBank|F2");
            hotkeys.Add("LoadScript|F4");
            hotkeys.Add("ToggleLagKiller|F6");
            hotkeys.Add("OpenConsole|Ctrl+Shift+C");
            hotkeys.Add("SearchScripts|Ctrl+Shift+F");
            hotkeys.Add("OpenCommandPalette|Ctrl+K");
            hotkeys.Add("ToggleFullscreen|F11");
            return;
        }

        if (!HasBinding("OpenBank"))
            hotkeys.Add("OpenBank|F2");
        if (!HasBinding("OpenCommandPalette"))
            hotkeys.Add("OpenCommandPalette|Ctrl+K");
        if (!HasBinding("ToggleFullscreen"))
            hotkeys.Add("ToggleFullscreen|F11");
    }

    private void AttachMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            return;

        if (!ReferenceEquals(_mainWindow, desktop.MainWindow))
        {
            if (_mainWindow is not null)
            {
                _mainWindow.RemoveHandler(InputElement.KeyDownEvent, MainWindowOnKeyDown);
                _mainWindow.Activated -= MainWindowOnActivated;
                _mainWindow.Deactivated -= MainWindowOnDeactivated;
            }

            _mainWindow = desktop.MainWindow;
            _mainWindow.RemoveHandler(InputElement.KeyDownEvent, MainWindowOnKeyDown);
            _mainWindow.AddHandler(InputElement.KeyDownEvent, MainWindowOnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            _mainWindow.Activated += MainWindowOnActivated;
            _mainWindow.Deactivated += MainWindowOnDeactivated;
            _isMainWindowActive = _mainWindow.IsActive;
        }
    }

    private void MainWindowOnActivated(object? sender, EventArgs e) => _isMainWindowActive = true;

    private void MainWindowOnDeactivated(object? sender, EventArgs e) => _isMainWindowActive = false;

    private void MainWindowOnKeyDown(object? sender, KeyEventArgs e)
    {
        HotKeyBinding[] bindings;
        lock (_bindingsLock)
            bindings = _bindings.ToArray();

        if (bindings.Length == 0)
            return;

        KeyModifiers modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        foreach (HotKeyBinding binding in bindings)
        {
            if (!IsKeyMatch(binding.Key, e.Key) || binding.Modifiers != modifiers)
                continue;

            if (ExecuteBinding(binding))
                e.Handled = true;
            return;
        }
    }

    private static bool IsKeyMatch(Key bindingKey, Key eventKey)
    {
        if (bindingKey == eventKey)
            return true;

        return (bindingKey, eventKey) switch
        {
            (Key.Subtract, Key.OemMinus) or (Key.OemMinus, Key.Subtract) => true,
            (Key.Add, Key.OemPlus) or (Key.OemPlus, Key.Add) => true,
            (Key.Decimal, Key.OemPeriod) or (Key.OemPeriod, Key.Decimal) => true,
            (Key.Divide, Key.OemQuestion) or (Key.OemQuestion, Key.Divide) => true,
            _ => false
        };
    }

    // The embedded Mugetsu child owns keyboard focus, so grab configured hotkeys on this X11 window.
    private void UpdateX11Grabs(List<HotKeyBinding> bindings)
    {
        if (_mainWindow is null)
            return;

        IPlatformHandle? handle = _mainWindow.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            if (!_waitingForMainWindow)
            {
                _waitingForMainWindow = true;
                _mainWindow.Opened += (_, _) => Reload();
            }
            return;
        }

        if (handle.HandleDescriptor != "XID")
            return;

        _x11Grabber ??= new X11HotKeyGrabber(OnX11HotKey);
        _x11Grabber.Update(handle.Handle, bindings.Select(b => new X11HotKeyGrabber.Gesture(b.Key, b.Modifiers)));
    }

    private void OnX11HotKey(X11HotKeyGrabber.Gesture gesture)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HotKeyBinding? binding;
            lock (_bindingsLock)
                binding = _bindings.FirstOrDefault(b => b.Key == gesture.Key && b.Modifiers == gesture.Modifiers);

            if (binding is not null)
                ExecuteBinding(binding);
        }, DispatcherPriority.Input);
    }

    private static KeyGestureBinding? ParseGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return null;

        gesture = gesture.Trim();
        KeyModifiers modifiers = KeyModifiers.None;

        while (true)
        {
            if (gesture.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase) || gesture.StartsWith("Ctl+", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
                gesture = gesture.Substring(gesture.IndexOf('+') + 1).TrimStart();
            }
            else if (gesture.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
                gesture = gesture.Substring(4).TrimStart();
            }
            else if (gesture.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
                gesture = gesture.Substring(6).TrimStart();
            }
            else
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(gesture))
            return null;

        string keyPart = gesture.Trim();

        Key key;
        switch (keyPart.ToUpperInvariant())
        {
            case "/":
            case "DIVIDE":
            case "NUMPADDIVIDE":
            case "NUMPAD/":
            case "NUMPAD_DIVIDE":
                key = Key.Divide;
                break;

            case "*":
            case "MULTIPLY":
            case "NUMPADMULTIPLY":
            case "NUMPAD*":
            case "NUMPAD_MULTIPLY":
                key = Key.Multiply;
                break;

            case "-":
            case "SUBTRACT":
            case "NUMPADSUBTRACT":
            case "NUMPAD-":
            case "NUMPAD_SUBTRACT":
                key = Key.Subtract;
                break;

            case "+":
            case "ADD":
            case "PLUS":
            case "NUMPADADD":
            case "NUMPADPLUS":
            case "NUMPAD+":
            case "NUMPAD_ADD":
                key = Key.Add;
                break;

            case ".":
            case "DECIMAL":
            case "NUMPADDECIMAL":
            case "NUMPAD.":
            case "NUMPAD_DECIMAL":
            case "NUMPADPERIOD":
                key = Key.Decimal;
                break;

            case "NUMPAD0":
            case "NUM0":
                key = Key.NumPad0;
                break;
            case "NUMPAD1":
            case "NUM1":
                key = Key.NumPad1;
                break;
            case "NUMPAD2":
            case "NUM2":
                key = Key.NumPad2;
                break;
            case "NUMPAD3":
            case "NUM3":
                key = Key.NumPad3;
                break;
            case "NUMPAD4":
            case "NUM4":
                key = Key.NumPad4;
                break;
            case "NUMPAD5":
            case "NUM5":
                key = Key.NumPad5;
                break;
            case "NUMPAD6":
            case "NUM6":
                key = Key.NumPad6;
                break;
            case "NUMPAD7":
            case "NUM7":
                key = Key.NumPad7;
                break;
            case "NUMPAD8":
            case "NUM8":
                key = Key.NumPad8;
                break;
            case "NUMPAD9":
            case "NUM9":
                key = Key.NumPad9;
                break;

            default:
                string normalized = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(keyPart.ToLowerInvariant());
                if (normalized.Length == 1 && char.IsDigit(normalized[0]))
                    normalized = $"D{normalized}";

                if (!Enum.TryParse(normalized, true, out key) || key == Key.None)
                    return null;
                break;
        }

        return new KeyGestureBinding(key, modifiers);
    }

    private static int? TryToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return 'A' + (int)(key - Key.A);
        if (key is >= Key.D0 and <= Key.D9)
            return 0x30 + (int)(key - Key.D0);
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return 0x60 + (int)(key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24)
            return 0x70 + (int)(key - Key.F1);

        return key switch
        {
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.Back => 0x08,
            Key.Delete => 0x2E,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,

            // Numpad operations
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Separator => 0x6C,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,

            // OEM keys
            Key.OemTilde => 0xC0,
            Key.OemMinus => 0xBD,
            Key.OemPlus => 0xBB,
            Key.OemOpenBrackets => 0xDB,
            Key.OemCloseBrackets => 0xDD,
            Key.OemSemicolon => 0xBA,
            Key.OemQuotes => 0xDE,
            Key.OemComma => 0xBC,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemPipe => 0xDC,
            _ => null
        };
    }

    private bool ExecuteBinding(HotKeyBinding binding)
    {
        if (_disposed)
            return false;

        try
        {
            if (!binding.Command.CanExecute(null))
                return false;

            binding.Command.Execute(null);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Hotkey '{binding.Binding}' failed: {ex}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_mainWindow is not null)
        {
            _mainWindow.RemoveHandler(InputElement.KeyDownEvent, MainWindowOnKeyDown);
            _mainWindow.Activated -= MainWindowOnActivated;
            _mainWindow.Deactivated -= MainWindowOnDeactivated;
        }


        _x11Grabber?.Dispose();
        lock (_bindingsLock)
            _bindings.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record HotKeyBinding(string Binding, IRelayCommand Command, Key Key, KeyModifiers Modifiers, int? VirtualKey);

    private sealed record KeyGestureBinding(Key Key, KeyModifiers Modifiers);
}
