using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Skua.App.Avalonia.Flash;

/// <summary>
/// Runs skua.swf in the Skua build of Mugetsu (see github.com/retr0gr4d3/skua-mugetsu) and
/// bridges ExternalInterface to it. The player connects back over a loopback TCP socket and both sides send one JSON
/// object per line: <c>{id,name,args}</c> calls a callback of the SWF and is answered with <c>{id,r}</c>, and
/// <c>{ev,args}</c> is an <c>ExternalInterface.call</c> from the SWF.
/// </summary>
public class FlashUtil : IFlashUtil, IRestartableGamePlayer
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
    private readonly object _sendLock = new();
    private readonly object _playerLock = new();
    private readonly object _telemetryLock = new();
    private TcpListener? _listener;
    private Process? _player;
    private int _acceptedPlayerPid;
    private MugetsuPerfSnapshot? _telemetry;
    private StreamWriter? _bridge;
    private string? _lastVisiblePayloadJson;
    private string? _lastFocusPayloadJson;
    private string? _lastResizePayloadJson;
    private string? _pendingFreeMemoryPayloadJson;
    private readonly Dictionary<string, string> _lastMugetsuSettings = new(StringComparer.Ordinal);
    private int _nextId;
    private readonly BridgeStats _stats = new();

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buf, nuint bufsiz);

    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public event FlashCallHandler? FlashCall;

    /// <summary>Embedded Wayland compositor socket name (<c>WAYLAND_DISPLAY</c>) for <c>Skua.WaylandHost</c> rendering.</summary>
    public string? WaylandDisplay { get; set; }
    public bool IsPlayerRunning => _player is { HasExited: false };

    public static void PreloadSwf()
    {
    }

    public void RestartGamePlayer()
    {
        InitializeFlash();
    }

    public bool CanRestartClient => false;

    public void RestartClient(bool runScript)
    {
    }

    private static string SwfPath
    {
        get
        {
            string localSwf = Path.Combine(AppContext.BaseDirectory, "skua.swf");
            if (File.Exists(localSwf))
                return localSwf;
            return Path.Combine(ClientFileSources.SkuaDIR, "skua.swf");
        }
    }

    private static string PlayerPath
    {
        get
        {
            string? envBin = Environment.GetEnvironmentVariable("MUGETSU_BIN");
            if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin))
                return envBin;

            const string playerExe = "Mugetsu";
            bool needsV2 = !System.Runtime.Intrinsics.X86.Avx2.IsSupported;

            // The regular player is built for x86-64-v3; use the bundled v2 build on older CPUs.
            string playerV2Subdir = Path.Combine(AppContext.BaseDirectory, "player-x86-64-v2", playerExe);
            if (needsV2 && File.Exists(playerV2Subdir))
                return playerV2Subdir;

            // Check player subdirectory
            string playerSubdir = Path.Combine(AppContext.BaseDirectory, "player", playerExe);
            if (File.Exists(playerSubdir))
                return playerSubdir;

            // Check local app directory
            string localBin = Path.Combine(AppContext.BaseDirectory, playerExe);
            if (File.Exists(localBin))
                return localBin;

            // Check workspace development cache path
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string playerRoot = Path.Combine(dir.FullName, "player");
                if (Directory.Exists(playerRoot))
                {
                    string? devPath = Directory
                        .EnumerateFiles(playerRoot, "Mugetsu", SearchOption.AllDirectories)
                        .FirstOrDefault(path => string.Equals(Path.GetFileName(path), playerExe, StringComparison.Ordinal));
                    if (devPath is not null)
                        return devPath;
                }
                dir = dir.Parent;
            }

            return playerSubdir;
        }
    }

    public void InitializeFlash()
    {
        if (!File.Exists(SwfPath))
            throw new FileNotFoundException($"Missing skua.swf ({SwfPath}). Build the AS3 client first so the game content can load.");
        if (!File.Exists(PlayerPath))
            throw new FileNotFoundException($"Missing the game player ({PlayerPath}). Rebuild BonxieMux so it downloads the player.");

        if (_listener is null)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        StopPlayer();
        ProcessStartInfo start = new(PlayerPath)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--skua-bridge");
        start.ArgumentList.Add($"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        string? wlDisplay = !string.IsNullOrWhiteSpace(WaylandDisplay)
            ? WaylandDisplay
            : Environment.GetEnvironmentVariable("BONXIE_WL_DISPLAY");
        string transportMode = "Wayland DMA-BUF";
        if (!string.IsNullOrWhiteSpace(wlDisplay))
        {
            start.Environment["WAYLAND_DISPLAY"] = wlDisplay;
            start.Environment["SKUA_EMBEDDED"] = "1";
            start.Environment.Remove("DISPLAY");
        }
        string? envGraphics = Environment.GetEnvironmentVariable("MUGETSU_GRAPHICS") ??
                              Environment.GetEnvironmentVariable("SKUA_GRAPHICS");
        if (!string.IsNullOrWhiteSpace(envGraphics))
        {
            start.ArgumentList.Add("-g");
            start.ArgumentList.Add(envGraphics.Trim().ToLowerInvariant());
        }
        string? extraArgs = Environment.GetEnvironmentVariable("MUGETSU_ARGS");
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            foreach (string arg in extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                start.ArgumentList.Add(arg);
        }
        start.ArgumentList.Add(SwfPath);
        Process proc = new() { StartInfo = start };
        lock (_playerLock)
        {
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start game player.");
            _player = proc;
            _acceptedPlayerPid = 0;
        }
        StartMugetsuTelemetryReader(proc, transportMode);
    }

    private void StopPlayer()
    {
        Process? oldPlayer;
        lock (_playerLock)
        {
            oldPlayer = _player;
            _player = null;
            _acceptedPlayerPid = 0;
        }

        try
        {
            if (oldPlayer is { HasExited: false })
                oldPlayer.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        oldPlayer?.Dispose();
        TryDeleteMugetsuTelemetryFile();
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => RunBridgeAsync(client));
        }
    }

    private bool TryAuthorizeBridgeClient(TcpClient client)
    {
        int expectedPid;
        lock (_playerLock)
        {
            if (_player is not { HasExited: false } activePlayer || _acceptedPlayerPid == activePlayer.Id)
                return false;
            expectedPid = activePlayer.Id;
        }

        if (client.Client.RemoteEndPoint is not IPEndPoint remoteEp ||
            client.Client.LocalEndPoint is not IPEndPoint localEp ||
            !IPAddress.IsLoopback(remoteEp.Address))
        {
            return false;
        }

        if (File.Exists("/proc/net/tcp"))
        {
            if (!VerifyLinuxLoopbackPeer(remoteEp.Port, localEp.Port, expectedPid))
                return false;
        }

        lock (_playerLock)
        {
            if (_player is not { HasExited: false } currentPlayer ||
                currentPlayer.Id != expectedPid ||
                _acceptedPlayerPid != 0)
            {
                return false;
            }

            _acceptedPlayerPid = expectedPid;
            return true;
        }
    }

    private static bool VerifyLinuxLoopbackPeer(int clientPort, int serverPort, int expectedPid)
    {
        try
        {
            string expectedLocal = $"0100007F:{clientPort:X4}";
            string expectedRem = $"0100007F:{serverPort:X4}";
            uint currentUid = geteuid();
            string? matchedInode = null;

            foreach (string line in File.ReadLines("/proc/net/tcp").Skip(1))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10)
                    continue;

                if (string.Equals(parts[1], expectedLocal, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parts[2], expectedRem, StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint peerUid) ||
                        peerUid != currentUid)
                    {
                        return false;
                    }

                    matchedInode = parts[9];
                    break;
                }
            }

            if (string.IsNullOrEmpty(matchedInode) || matchedInode == "0")
                return false;

            string fdDir = $"/proc/{expectedPid}/fd";
            if (Directory.Exists(fdDir))
            {
                string expectedTarget = $"socket:[{matchedInode}]";
                byte[] buf = new byte[128];
                try
                {
                    foreach (string fdEntry in Directory.EnumerateFileSystemEntries(fdDir))
                    {
                        nint len = readlink(fdEntry, buf, (nuint)buf.Length);
                        if (len > 0 && Encoding.ASCII.GetString(buf, 0, (int)len) == expectedTarget)
                            return true;
                    }
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // Fall back to UID + single-client PID gate if /proc/<pid>/fd enumeration is restricted.
                    return true;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunBridgeAsync(TcpClient client)
    {
        using (client)
        {
            if (!TryAuthorizeBridgeClient(client))
            {
                Trace.WriteLine("Rejected unauthorized connection to Flash bridge.");
                return;
            }

            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            // A restarted player connects anew; whatever the old one still owed will never come.
            lock (_sendLock)
            {
                Interlocked.Exchange(ref _bridge, writer)?.Dispose();
                try
                {
                    if (_lastResizePayloadJson != null)
                        writer.WriteLine(_lastResizePayloadJson);
                    if (_lastVisiblePayloadJson != null)
                        writer.WriteLine(_lastVisiblePayloadJson);
                    if (_lastFocusPayloadJson != null)
                        writer.WriteLine(_lastFocusPayloadJson);
                    foreach (string settingJson in _lastMugetsuSettings.Values)
                        writer.WriteLine(settingJson);
                    if (_pendingFreeMemoryPayloadJson != null)
                    {
                        writer.WriteLine(_pendingFreeMemoryPayloadJson);
                        _pendingFreeMemoryPayloadJson = null;
                    }
                }
                catch { }
            }
            FailPending();

            try
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync() is { } line)
                    Receive(JObject.Parse(line), line.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
            {
                Trace.WriteLine($"Game player bridge closed: {ex.Message}");
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _bridge, null, writer) == writer)
                    FailPending();
            }
        }
    }

    private void Receive(JObject msg, int chars)
    {
        if (msg["id"] is { } id)
        {
            if (_pending.TryRemove((int)id, out TaskCompletionSource<JObject>? tcs))
                tcs.TrySetResult(msg);
            return;
        }

        string function = (string)msg["ev"]!;
        _stats.RecordEvent(function, chars);
        object[] args = msg["args"] is JArray arr ? arr.Select(FromJson).ToArray() : [];
        // Handlers expect calls on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                FlashCall?.Invoke(function, args);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Flash call handler for '{function}' failed: {ex}");
            }
        });
    }

    public void SendInput(object payload)
    {
        try
        {
            JObject obj = JObject.FromObject(payload);
            string? input = obj["input"]?.Value<string>();
            string json = obj.ToString(Formatting.None);
            lock (_sendLock)
            {
                if (input == "visible")
                    _lastVisiblePayloadJson = json;
                else if (input == "focus")
                    _lastFocusPayloadJson = json;
                else if (input == "resize")
                    _lastResizePayloadJson = json;
                else if (input == "mugetsu_setting")
                {
                    string? settingName = obj["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(settingName))
                        _lastMugetsuSettings[settingName] = json;
                }
                else if (input == "free_memory" && _bridge is null)
                {
                    _pendingFreeMemoryPayloadJson = json;
                }

                if (_bridge is { } writer)
                    writer.WriteLine(json);
            }
        }
        catch { }
    }

    private void FailPending()
    {
        foreach (int id in _pending.Keys)
            if (_pending.TryRemove(id, out TaskCompletionSource<JObject>? tcs))
                tcs.TrySetResult(new JObject());
    }

    public string Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is T value ? value : GetDefaultValue<T>();
        }
        catch
        {
            return GetDefaultValue<T>();
        }
    }

    private static T GetDefaultValue<T>()
    {
        object? defaultValue = DefaultProvider.GetDefault<T>(typeof(T));
        return defaultValue is T typed ? typed : default!;
    }

    public bool TryCall<T>(string function, TimeSpan timeout, CancellationToken cancellationToken, out T? result, params object[] args)
    {
        if (TryCallInternal(function, typeof(T), timeout, cancellationToken, throwOnTimeout: false, out object? raw, args))
        {
            result = raw is T value ? value : GetDefaultValue<T>();
            return true;
        }

        result = GetDefaultValue<T>();
        return false;
    }

    public object Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            TryCallInternal(function, type, CallTimeout, CancellationToken.None, throwOnTimeout: true, out object? value, args);
            return value!;
        }
        catch (Exception e)
        {
            if (function != "isKicked")
            {
                object[] safeArgs = string.Equals(function, "login", StringComparison.OrdinalIgnoreCase)
                    ? args.Select((a, idx) => idx > 0 ? (object)"***" : a).ToArray()
                    : args;
                _messenger.Send<FlashErrorMessage>(new(e, function, safeArgs));
            }
            return default!;
        }
    }

    private bool TryCallInternal(
        string function,
        Type type,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool throwOnTimeout,
        out object? value,
        params object[] args)
    {
        value = default;
        if (cancellationToken.IsCancellationRequested || _bridge is not { } writer)
            return false;

        int id = Interlocked.Increment(ref _nextId);
        long startTimestamp = Stopwatch.GetTimestamp();
        TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            string request = JsonConvert.SerializeObject(new { id, name = function, args = args.Select(ToJs) });
            lock (_sendLock)
                writer.WriteLine(request);

            bool completed;
            try
            {
                completed = tcs.Task.Wait((int)Math.Max(1, timeout.TotalMilliseconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (!completed)
            {
                if (throwOnTimeout)
                    throw new TimeoutException($"No answer from the game player within {timeout.TotalSeconds}s.");
                return false;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }

        JObject response = tcs.Task.Result;
        if (!response.ContainsKey("id") && !response.ContainsKey("r"))
            return false;

        string? result = response["r"] switch
        {
            null or { Type: JTokenType.Null } => null,
            JValue { Value: bool b } => b ? "true" : "false",
            JValue v => Convert.ToString(v.Value, CultureInfo.InvariantCulture),
            JToken t => t.ToString(Formatting.None)
        };
        if (_stats.Enabled)
        {
            // getGameObject & co. are generic; the path tells which object was read.
            string key = function.StartsWith("getGameObject", StringComparison.Ordinal) && args.Length > 0 && args[0] is string path
                ? $"{function} {path}"
                : function;
            _stats.RecordCall(key, startTimestamp, result?.Length ?? 0);
        }
        try
        {
            value = result is null ? default : Convert.ChangeType(result, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch when (!throwOnTimeout)
        {
            value = default;
            return false;
        }
    }

    private static object? ToJs(object? o)
    {
        return o switch
        {
            null or bool or double or float or long or int or string => o,
            ExpandoObject e => e.ToDictionary(kvp => kvp.Key, kvp => ToJs(kvp.Value)),
            Array a => a.Cast<object?>().Select(ToJs).ToArray(),
            _ => o.ToString()
        };
    }

    private static object FromJson(JToken t)
    {
        switch (t.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (float)t;

            case JTokenType.Boolean:
                return (bool)t;

            case JTokenType.Null:
            case JTokenType.Undefined:
                return null!;

            case JTokenType.Array:
                return t.Select(FromJson).ToArray();

            case JTokenType.Object:
                IDictionary<string, object> d = new ExpandoObject();
                foreach (JProperty p in ((JObject)t).Properties())
                    d[p.Name] = FromJson(p.Value);
                return d;

            default:
                return (string)t!;
        }
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;

            case "true":
                return true;

            case "false":
                return false;

            case "null":
                return null!;

            case "array":
                return el.Elements().Select(e => FromFlashXml(e)).ToArray();

            case "object":
                dynamic d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;

            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex BackendRegex = new(@"Using (?:preferred|fallback) backend (.+)", RegexOptions.Compiled);
    private static readonly Regex GraphicsApiRegex = new(@"Using graphics API (\S+) on (.+?) \(type:\s*([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex SurfaceFormatRegex = new(@"Using surface format (.+)", RegexOptions.Compiled);
    private static readonly Regex InterpToggleRegex = new(@"Frame interpolation turned (on|off)", RegexOptions.Compiled);
    private static readonly Regex MonitorHzRegex = new(@"monitor refreshes at ([0-9.]+)\s*Hz", RegexOptions.Compiled);
    private static readonly Regex PerfSummaryRegex = new(
        @"game fps ([0-9.]+), drawn ([0-9.]+), presented ([0-9.]+) \| frame avg ([0-9.]+)ms max ([0-9.]+)ms \| worst gap ([0-9.]+)ms, hitches ([0-9]+) \| render avg ([0-9.]+)ms max ([0-9.]+)ms",
        RegexOptions.Compiled);
    private static readonly Regex PerDrawnRegex = new(@"per drawn frame:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex RenderBreakdownRegex = new(@"render time per drawn frame \(ms\):\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex InterpInlineRegex = new(@";\s*interpolation\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex InterpReplayRegex = new(@"interpolation replay:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ObjectsRegex = new(@"^objects:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex MemoryRegex = new(@"^memory:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex MemNumbersRegex = new(@"process commit ([0-9]+) MB,\s*rust heap ([0-9]+) MB", RegexOptions.Compiled);
    private static readonly Regex SlowFrameRegex = new(@"slow frame\b(.*)", RegexOptions.Compiled);
    private static readonly Regex SlowRenderRegex = new(@"slow render\b(.*)", RegexOptions.Compiled);

    private static string GetMugetsuPerfPath(int childPid) =>
        Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), $"skua-mugetsu-perf-{childPid}.json");

    private void StartMugetsuTelemetryReader(Process proc, string transportMode)
    {
        int mugetsuPid = 0;
        try { mugetsuPid = proc.Id; } catch { }

        lock (_telemetryLock)
        {
            _telemetry = new MugetsuPerfSnapshot
            {
                ChildPid = Environment.ProcessId,
                MugetsuPid = mugetsuPid,
                TransportMode = transportMode,
                StartedAtUtc = DateTime.UtcNow
            };
            WriteMugetsuTelemetrySnapshotLocked();
        }

        _ = Task.Run(() => PumpMugetsuStreamAsync(proc.StandardOutput, Console.Out));
        _ = Task.Run(() => PumpMugetsuStreamAsync(proc.StandardError, Console.Error));
    }

    private async Task PumpMugetsuStreamAsync(StreamReader reader, TextWriter echo)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try { echo.WriteLine(line); } catch { }
                ParseMugetsuLogLine(line);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private void ParseMugetsuLogLine(string rawLine)
    {
        string clean = AnsiEscapeRegex.Replace(rawLine, string.Empty).Trim();
        if (string.IsNullOrEmpty(clean))
            return;

        lock (_telemetryLock)
        {
            MugetsuPerfSnapshot? snap = _telemetry;
            if (snap == null)
                return;

            bool dirty = false;
            Match m;

            if ((m = BackendRegex.Match(clean)).Success)
            {
                snap.PreferredBackend = m.Groups[1].Value.Trim();
                dirty = true;
            }
            if ((m = GraphicsApiRegex.Match(clean)).Success)
            {
                string api = m.Groups[1].Value.Trim();
                snap.GraphicsApi = api.Equals("gl", StringComparison.OrdinalIgnoreCase)
                    ? "OpenGL"
                    : api.Equals("vulkan", StringComparison.OrdinalIgnoreCase)
                        ? "Vulkan"
                        : api;
                snap.GpuAdapter = m.Groups[2].Value.Trim();
                snap.GpuType = m.Groups[3].Value.Trim();
                dirty = true;
            }
            if ((m = SurfaceFormatRegex.Match(clean)).Success)
            {
                snap.SurfaceFormat = m.Groups[1].Value.Trim();
                dirty = true;
            }
            if ((m = InterpToggleRegex.Match(clean)).Success)
            {
                snap.InterpolationState = m.Groups[1].Value.Trim();
                dirty = true;
            }
            if ((m = MonitorHzRegex.Match(clean)).Success)
            {
                int hz = (int)Math.Round(ParseDoubleInvariant(m.Groups[1].Value));
                if (hz > 0)
                {
                    snap.MonitorRefreshHz = hz;
                    dirty = true;
                }
            }

            string perfPayload = string.Empty;
            if (clean.Contains("ruffle_aqw_perf", StringComparison.Ordinal))
            {
                int colonIdx = clean.IndexOf("ruffle_aqw_perf:", StringComparison.Ordinal);
                perfPayload = colonIdx >= 0
                    ? clean[(colonIdx + "ruffle_aqw_perf:".Length)..].Trim()
                    : clean;

                snap.RecentPerfLogs.Add($"[{DateTime.Now:HH:mm:ss}] {perfPayload}");
                if (snap.RecentPerfLogs.Count > 60)
                    snap.RecentPerfLogs.RemoveAt(0);
                dirty = true;
            }

            if ((m = PerfSummaryRegex.Match(clean)).Success)
            {
                snap.GameFps = ParseDoubleInvariant(m.Groups[1].Value);
                snap.DrawnFps = ParseDoubleInvariant(m.Groups[2].Value);
                snap.PresentedFps = ParseDoubleInvariant(m.Groups[3].Value);
                snap.FrameAvgMs = ParseDoubleInvariant(m.Groups[4].Value);
                snap.FrameMaxMs = ParseDoubleInvariant(m.Groups[5].Value);
                snap.WorstGapMs = ParseDoubleInvariant(m.Groups[6].Value);
                snap.Hitches = int.TryParse(m.Groups[7].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : 0;
                snap.RenderAvgMs = ParseDoubleInvariant(m.Groups[8].Value);
                snap.RenderMaxMs = ParseDoubleInvariant(m.Groups[9].Value);
                snap.WindowCount++;
                snap.LastPerfUpdateUtc = DateTime.UtcNow;
                dirty = true;
            }
            else if ((m = PerDrawnRegex.Match(clean)).Success)
            {
                snap.PerDrawnFrameCounters = m.Groups[1].Value.Trim();
                dirty = true;
            }
            else if ((m = RenderBreakdownRegex.Match(clean)).Success)
            {
                string breakdown = m.Groups[1].Value.Trim();
                snap.RenderTimeBreakdown = breakdown;
                Match im = InterpInlineRegex.Match(breakdown);
                if (im.Success)
                    snap.InterpolationState = im.Groups[1].Value.Trim();
                dirty = true;
            }
            else if ((m = InterpReplayRegex.Match(clean)).Success)
            {
                snap.InterpolationReplay = m.Groups[1].Value.Trim();
                dirty = true;
            }
            else if (!string.IsNullOrEmpty(perfPayload) && (m = ObjectsRegex.Match(perfPayload)).Success)
            {
                snap.ObjectsReport = m.Groups[1].Value.Trim();
                dirty = true;
            }
            else if (!string.IsNullOrEmpty(perfPayload) && (m = MemoryRegex.Match(perfPayload)).Success)
            {
                string memText = m.Groups[1].Value.Trim();
                snap.MemoryReport = memText;
                Match nm = MemNumbersRegex.Match(memText);
                if (nm.Success)
                {
                    if (long.TryParse(nm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long commitMb))
                        snap.ProcessCommitMb = commitMb;
                    if (long.TryParse(nm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long heapMb))
                        snap.RustHeapMb = heapMb;
                }
                dirty = true;
            }
            else if ((m = SlowFrameRegex.Match(clean)).Success)
            {
                snap.SlowFrameCount++;
                snap.LastSlowEvent = $"[{DateTime.Now:HH:mm:ss}] slow frame{m.Groups[1].Value}";
                dirty = true;
            }
            else if ((m = SlowRenderRegex.Match(clean)).Success)
            {
                snap.SlowRenderCount++;
                snap.LastSlowEvent = $"[{DateTime.Now:HH:mm:ss}] slow render{m.Groups[1].Value}";
                dirty = true;
            }

            if (dirty)
                WriteMugetsuTelemetrySnapshotLocked();
        }
    }

    private static double ParseDoubleInvariant(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private void WriteMugetsuTelemetrySnapshotLocked()
    {
        if (_telemetry == null)
            return;
        try
        {
            string path = GetMugetsuPerfPath(Environment.ProcessId);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(_telemetry));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private void TryDeleteMugetsuTelemetryFile()
    {
        lock (_telemetryLock)
        {
            _telemetry = null;
        }
        try
        {
            string path = GetMugetsuPerfPath(Environment.ProcessId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private sealed class MugetsuPerfSnapshot
    {
        public int ChildPid { get; set; }
        public int MugetsuPid { get; set; }
        public string TransportMode { get; set; } = string.Empty;
        public DateTime StartedAtUtc { get; set; }
        public DateTime? LastPerfUpdateUtc { get; set; }
        public string? PreferredBackend { get; set; }
        public string? GraphicsApi { get; set; }
        public string? GpuAdapter { get; set; }
        public string? GpuType { get; set; }
        public string? SurfaceFormat { get; set; }
        public string? InterpolationState { get; set; }
        public int MonitorRefreshHz { get; set; } = 60;
        public double GameFps { get; set; }
        public double DrawnFps { get; set; }
        public double PresentedFps { get; set; }
        public double FrameAvgMs { get; set; }
        public double FrameMaxMs { get; set; }
        public double WorstGapMs { get; set; }
        public int Hitches { get; set; }
        public double RenderAvgMs { get; set; }
        public double RenderMaxMs { get; set; }
        public int WindowCount { get; set; }
        public string? PerDrawnFrameCounters { get; set; }
        public string? RenderTimeBreakdown { get; set; }
        public string? InterpolationReplay { get; set; }
        public string? ObjectsReport { get; set; }
        public string? MemoryReport { get; set; }
        public long ProcessCommitMb { get; set; }
        public long RustHeapMb { get; set; }
        public int SlowFrameCount { get; set; }
        public int SlowRenderCount { get; set; }
        public string? LastSlowEvent { get; set; }
        public List<string> RecentPerfLogs { get; } = new();
    }

    public void Dispose()
    {
        StopPlayer();
        _listener?.Stop();
        _listener = null;
        _bridge?.Dispose();
        FailPending();
    }
}
