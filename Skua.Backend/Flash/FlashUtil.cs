using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using STJ = System.Text.Json;

namespace Skua.Backend.Flash;

public class FlashUtil : IFlashUtil, IRestartableGamePlayer
{
    /// <summary>
    /// Optional UI-thread dispatcher hook wired by the presentation layer so FlashCall events
    /// preserve UI-thread affinity without coupling Skua.Backend to any UI framework.
    /// </summary>
    public static Action<Action> UiPost { get; set; } = static action => action();
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly long ShortReadCacheTtlTicks = (long)(Stopwatch.Frequency * 0.025);
    private static readonly long StaticReadCacheTtlTicks = (long)(Stopwatch.Frequency * 0.250);

    private static readonly HashSet<string> ShortReadCacheFunctions = new(StringComparer.Ordinal)
    {
        "isNull",
        "isTrue",
        "isLoggedIn",
        "getGameObject",
        "getGameObjectKey",
        "getTargetMonster",
        "availableMonsters",
        "getMonsters",
        "getSubjectAuras",
        "GetAuraSnapshots",
        "GetAuraRuleStacks",
        "HasAnyActiveAura",
        "getAvatar",
        "hasInventoryCategories",
        "inventoryBagUsedSlots",
        "inventoryMiscSlots",
        "inventoryMiscUsedSlots"
    };

    private static readonly HashSet<string> StaticReadCacheFunctions = new(StringComparer.Ordinal)
    {
        "getGameObjectS",
        "gender"
    };

    private static readonly string[] VolatileReadPathMarkers =
    [
        "mcConnDetail",
        "mcLogin",
        "ui.mcPopup",
        "world.mapLoadInProgress",
        "artLoaded",
        "isKicked"
    ];

    private sealed class PendingCall
    {
        public readonly ManualResetEventSlim Ready = new(false);
        public bool HasResult;
        public string? RawResult;

        public void Reset()
        {
            Ready.Reset();
            HasResult = false;
            RawResult = null;
        }
    }

    private readonly record struct ReadCacheKey(string Function, string Arg0, string Arg1);
    private readonly record struct CachedReadValue(long Epoch, long ExpiresTimestamp, string? RawResult);

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly ConcurrentDictionary<int, PendingCall> _pending = new();
    private readonly ConcurrentBag<PendingCall> _pendingPool = new();
    private readonly ConcurrentDictionary<ReadCacheKey, CachedReadValue> _readCache = new();
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(512);
    private readonly object _sendLock = new();
    private readonly object _playerLock = new();
    private readonly object _telemetryLock = new();
    private Socket? _unixListener;
    private string? _unixSocketPath;
    private Process? _player;
    private int _acceptedPlayerPid;
    private MugetsuPerfSnapshot? _telemetry;
    private NetworkStream? _bridgeStream;
    private byte[]? _lastVisiblePayloadBytes;
    private byte[]? _lastFocusPayloadBytes;
    private byte[]? _lastResizePayloadBytes;
    private byte[]? _pendingFreeMemoryPayloadBytes;
    private readonly Dictionary<string, byte[]> _lastMugetsuSettings = new(StringComparer.Ordinal);
    private long _cacheEpoch;
    private int _nextId;
    private readonly BridgeStats _stats = new();

    private const int SOL_SOCKET = 1;
    private const int SO_PEERCRED = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxUcred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, out LinuxUcred optval, ref uint optlen);

    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public event FlashCallHandler? FlashCall;

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

            string playerV2Subdir = Path.Combine(AppContext.BaseDirectory, "player-x86-64-v2", playerExe);
            if (needsV2 && File.Exists(playerV2Subdir))
                return playerV2Subdir;

            string playerSubdir = Path.Combine(AppContext.BaseDirectory, "player", playerExe);
            if (File.Exists(playerSubdir))
                return playerSubdir;

            string localBin = Path.Combine(AppContext.BaseDirectory, playerExe);
            if (File.Exists(localBin))
                return localBin;

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

        if (_unixListener is null)
        {
            Directory.CreateDirectory(ClientFileSources.TabsDIR);
            _unixSocketPath = Path.Combine(ClientFileSources.TabsDIR, $"bridge_{Environment.ProcessId}.sock");
            try
            {
                if (File.Exists(_unixSocketPath))
                    File.Delete(_unixSocketPath);
            }
            catch { }
            Socket unixSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unixSocket.Bind(new UnixDomainSocketEndPoint(_unixSocketPath));
            try { File.SetUnixFileMode(_unixSocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            unixSocket.Listen(4);
            _unixListener = unixSocket;
            _ = Task.Run(AcceptUnixLoopAsync);
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
        start.ArgumentList.Add($"unix://{_unixSocketPath}");
        string? wlDisplay = !string.IsNullOrWhiteSpace(WaylandDisplay)
            ? WaylandDisplay
            : Environment.GetEnvironmentVariable("BONXIE_WL_DISPLAY");
        const string transportMode = "Wayland DMA-BUF (AF_UNIX)";
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
        catch {  }
        oldPlayer?.Dispose();
        TryDeleteMugetsuTelemetryFile();
    }

    private async Task AcceptUnixLoopAsync()
    {
        while (_unixListener is { } listener)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => RunBridgeUnixAsync(client));
        }
    }

    private bool TryAuthorizeUnixBridgeClient(Socket client)
    {
        int expectedPid;
        lock (_playerLock)
        {
            if (_player is not { HasExited: false } activePlayer || _acceptedPlayerPid == activePlayer.Id)
                return false;
            expectedPid = activePlayer.Id;
        }

        uint len = (uint)Marshal.SizeOf<LinuxUcred>();
        if (getsockopt((int)client.Handle, SOL_SOCKET, SO_PEERCRED, out LinuxUcred cred, ref len) != 0)
            return false;

        if (cred.Uid != geteuid() || cred.Pid != expectedPid)
            return false;

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

    private async Task RunBridgeUnixAsync(Socket client)
    {
        using (client)
        {
            if (!TryAuthorizeUnixBridgeClient(client))
            {
                Trace.WriteLine("Rejected unauthorized AF_UNIX connection to Flash bridge.");
                return;
            }

            using NetworkStream stream = new(client, ownsSocket: false);
            await RunBridgeStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private async Task RunBridgeStreamAsync(NetworkStream stream)
    {
        lock (_sendLock)
        {
            Interlocked.Exchange(ref _bridgeStream, stream)?.Dispose();
            try
            {
                if (_lastResizePayloadBytes != null)
                    stream.Write(_lastResizePayloadBytes);
                if (_lastVisiblePayloadBytes != null)
                    stream.Write(_lastVisiblePayloadBytes);
                if (_lastFocusPayloadBytes != null)
                    stream.Write(_lastFocusPayloadBytes);
                foreach (byte[] settingBytes in _lastMugetsuSettings.Values)
                    stream.Write(settingBytes);
                if (_pendingFreeMemoryPayloadBytes != null)
                {
                    stream.Write(_pendingFreeMemoryPayloadBytes);
                    _pendingFreeMemoryPayloadBytes = null;
                }
            }
            catch { }
        }
        FailPending();

        try
        {
            byte[] buffer = new byte[8192];
            int filled = 0;
            while (true)
            {
                int searchStart = 0;
                while (true)
                {
                    int rel = Array.IndexOf(buffer, (byte)'\n', searchStart, filled - searchStart);
                    if (rel < 0)
                        break;

                    int lineEnd = (rel > searchStart && buffer[rel - 1] == (byte)'\r') ? rel - 1 : rel;
                    int lineLen = lineEnd - searchStart;
                    if (lineLen > 0)
                    {
                        using STJ.JsonDocument doc = STJ.JsonDocument.Parse(buffer.AsMemory(searchStart, lineLen));
                        Receive(doc.RootElement, lineLen);
                    }
                    searchStart = rel + 1;
                }

                if (searchStart > 0)
                {
                    int remaining = filled - searchStart;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, searchStart, buffer, 0, remaining);
                    filled = remaining;
                }

                if (filled == buffer.Length)
                    Array.Resize(ref buffer, buffer.Length * 2);

                int bytesRead = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled)).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;
                filled += bytesRead;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or STJ.JsonException)
        {
            Trace.WriteLine($"Game player bridge closed: {ex.Message}");
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _bridgeStream, null, stream) == stream)
                FailPending();
        }
    }

    private void Receive(STJ.JsonElement msg, int chars)
    {
        if (msg.TryGetProperty("id", out STJ.JsonElement idProp) && idProp.TryGetInt32(out int id))
        {
            if (_pending.TryRemove(id, out PendingCall? pending))
            {
                pending.RawResult = ExtractRawResult(msg);
                pending.HasResult = true;
                pending.Ready.Set();
            }
            return;
        }

        if (!msg.TryGetProperty("ev", out STJ.JsonElement evProp))
            return;

        InvalidateReadCache();
        string function = evProp.GetString()!;
        _stats.RecordEvent(function, chars);
        object[] args = msg.TryGetProperty("args", out STJ.JsonElement argsProp) && argsProp.ValueKind == STJ.JsonValueKind.Array
            ? ConvertArgsArray(argsProp)
            : [];
        UiPost(() =>
        {
            FlashCallHandler? subscribers = FlashCall;
            if (subscribers is null)
                return;

            foreach (FlashCallHandler handler in subscribers.GetInvocationList().Cast<FlashCallHandler>())
            {
                try
                {
                    handler(function, args);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Flash call handler for '{function}' failed: {ex}");
                }
            }
        });
    }

    private static string? ExtractRawResult(STJ.JsonElement msg)
    {
        if (!msg.TryGetProperty("r", out STJ.JsonElement rProp))
            return null;

        return rProp.ValueKind switch
        {
            STJ.JsonValueKind.Null or STJ.JsonValueKind.Undefined => null,
            STJ.JsonValueKind.True => "true",
            STJ.JsonValueKind.False => "false",
            STJ.JsonValueKind.String => rProp.GetString(),
            _ => rProp.GetRawText()
        };
    }

    private static object[] ConvertArgsArray(STJ.JsonElement arr)
    {
        int count = arr.GetArrayLength();
        if (count == 0)
            return [];

        object[] result = new object[count];
        int i = 0;
        foreach (STJ.JsonElement item in arr.EnumerateArray())
            result[i++] = FromJsonElement(item);
        return result;
    }

    public void SendInput(object payload)
    {
        try
        {
            byte[] utf8 = STJ.JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] lineBytes = new byte[utf8.Length + 1];
            Buffer.BlockCopy(utf8, 0, lineBytes, 0, utf8.Length);
            lineBytes[utf8.Length] = (byte)'\n';

            string? input = null;
            string? settingName = null;
            using (STJ.JsonDocument doc = STJ.JsonDocument.Parse(utf8))
            {
                if (doc.RootElement.ValueKind == STJ.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("input", out STJ.JsonElement inpEl) && inpEl.ValueKind == STJ.JsonValueKind.String)
                        input = inpEl.GetString();
                    if (input == "mugetsu_setting" && doc.RootElement.TryGetProperty("name", out STJ.JsonElement nameEl) && nameEl.ValueKind == STJ.JsonValueKind.String)
                        settingName = nameEl.GetString();
                }
            }

            lock (_sendLock)
            {
                if (input == "visible")
                    _lastVisiblePayloadBytes = lineBytes;
                else if (input == "focus")
                    _lastFocusPayloadBytes = lineBytes;
                else if (input == "resize")
                    _lastResizePayloadBytes = lineBytes;
                else if (input == "mugetsu_setting" && !string.IsNullOrEmpty(settingName))
                    _lastMugetsuSettings[settingName] = lineBytes;
                else if (input == "free_memory" && _bridgeStream is null)
                    _pendingFreeMemoryPayloadBytes = lineBytes;

                if (_bridgeStream is { } stream)
                    stream.Write(lineBytes);
            }
        }
        catch { }
    }

    private void InvalidateReadCache() => Interlocked.Increment(ref _cacheEpoch);

    private void FailPending()
    {
        InvalidateReadCache();
        _readCache.Clear();
        foreach (int id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out PendingCall? pending))
            {
                pending.HasResult = false;
                pending.RawResult = null;
                pending.Ready.Set();
            }
        }
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

    private static bool TryGetReadCacheKey(string function, object[] args, out ReadCacheKey key, out long ttlTicks)
    {
        ttlTicks = 0;
        if (StaticReadCacheFunctions.Contains(function))
            ttlTicks = StaticReadCacheTtlTicks;
        else if (ShortReadCacheFunctions.Contains(function))
            ttlTicks = ShortReadCacheTtlTicks;
        else
        {
            key = default;
            return false;
        }

        if (args.Length > 2)
        {
            key = default;
            return false;
        }

        string arg0 = string.Empty;
        string arg1 = string.Empty;
        if (args.Length >= 1)
        {
            if (!TryFormatCacheArg(args[0], out arg0))
            {
                key = default;
                return false;
            }
            for (int i = 0; i < VolatileReadPathMarkers.Length; i++)
            {
                if (arg0.Contains(VolatileReadPathMarkers[i], StringComparison.Ordinal))
                {
                    key = default;
                    return false;
                }
            }
        }
        if (args.Length == 2 && !TryFormatCacheArg(args[1], out arg1))
        {
            key = default;
            return false;
        }

        key = new ReadCacheKey(function, arg0, arg1);
        return true;
    }

    private static bool TryFormatCacheArg(object? arg, out string formatted)
    {
        switch (arg)
        {
            case string s:
                formatted = s;
                return true;
            case int i:
                formatted = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case bool b:
                formatted = b ? "1" : "0";
                return true;
            default:
                formatted = string.Empty;
                return false;
        }
    }

    private PendingCall RentPendingCall()
    {
        if (_pendingPool.TryTake(out PendingCall? pending))
        {
            pending.Reset();
            return pending;
        }
        return new PendingCall();
    }

    private void ReturnPendingCall(PendingCall pending)
    {
        if (_pendingPool.Count < 16)
            _pendingPool.Add(pending);
        else
            pending.Ready.Dispose();
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
        if (cancellationToken.IsCancellationRequested || _bridgeStream is null)
            return false;

        long startTimestamp = Stopwatch.GetTimestamp();
        bool cacheable = TryGetReadCacheKey(function, args, out ReadCacheKey cacheKey, out long cacheTtlTicks);
        if (cacheable)
        {
            long curEpoch = Volatile.Read(ref _cacheEpoch);
            if (_readCache.TryGetValue(cacheKey, out CachedReadValue cached) &&
                cached.Epoch == curEpoch &&
                startTimestamp < cached.ExpiresTimestamp)
            {
                if (_stats.Enabled)
                {
                    string statKey = function.StartsWith("getGameObject", StringComparison.Ordinal) && args.Length > 0 && args[0] is string path
                        ? $"{function} {path}"
                        : function;
                    _stats.RecordCall(statKey, startTimestamp, cached.RawResult?.Length ?? 0);
                }

                try
                {
                    value = cached.RawResult is null ? default : Convert.ChangeType(cached.RawResult, type, CultureInfo.InvariantCulture);
                    return true;
                }
                catch when (!throwOnTimeout)
                {
                    value = default;
                    return false;
                }
            }
        }
        else
        {
            InvalidateReadCache();
        }

        long callEpoch = cacheable ? Volatile.Read(ref _cacheEpoch) : 0;
        int id = Interlocked.Increment(ref _nextId);
        PendingCall pending = RentPendingCall();
        _pending[id] = pending;
        bool hasResult = false;
        string? result = null;
        try
        {
            lock (_sendLock)
            {
                if (_bridgeStream is not { } stream)
                    return false;

                _sendBuffer.Clear();
                using (STJ.Utf8JsonWriter writer = new(_sendBuffer))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    writer.WriteString("name", function);
                    writer.WritePropertyName("args");
                    writer.WriteStartArray();
                    for (int i = 0; i < args.Length; i++)
                        WriteJsArg(writer, args[i]);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                Span<byte> nl = _sendBuffer.GetSpan(1);
                nl[0] = (byte)'\n';
                _sendBuffer.Advance(1);
                stream.Write(_sendBuffer.WrittenSpan);
            }

            bool completed;
            try
            {
                completed = pending.Ready.Wait((int)Math.Max(1, timeout.TotalMilliseconds), cancellationToken);
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

            hasResult = pending.HasResult;
            result = pending.RawResult;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            ReturnPendingCall(pending);
        }

        if (!hasResult)
            return false;

        if (!cacheable)
        {
            InvalidateReadCache();
        }
        else if (Volatile.Read(ref _cacheEpoch) == callEpoch)
        {
            if (_readCache.Count > 256)
                _readCache.Clear();
            _readCache[cacheKey] = new CachedReadValue(callEpoch, Stopwatch.GetTimestamp() + cacheTtlTicks, result);
        }

        if (_stats.Enabled)
        {
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

    private static void WriteJsArg(STJ.Utf8JsonWriter writer, object? o)
    {
        switch (o)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case IDictionary<string, object?> dict:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object?> kvp in dict)
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteJsArg(writer, kvp.Value);
                }
                writer.WriteEndObject();
                break;
            case Array a:
                writer.WriteStartArray();
                for (int i = 0; i < a.Length; i++)
                    WriteJsArg(writer, a.GetValue(i));
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(o.ToString());
                break;
        }
    }

    private static object FromJsonElement(STJ.JsonElement t)
    {
        switch (t.ValueKind)
        {
            case STJ.JsonValueKind.Number:
                return t.TryGetInt32(out int i) ? i : (float)t.GetDouble();

            case STJ.JsonValueKind.True:
                return true;

            case STJ.JsonValueKind.False:
                return false;

            case STJ.JsonValueKind.Null:
            case STJ.JsonValueKind.Undefined:
                return null!;

            case STJ.JsonValueKind.Array:
                return ConvertArgsArray(t);

            case STJ.JsonValueKind.Object:
                IDictionary<string, object> d = new ExpandoObject();
                foreach (STJ.JsonProperty p in t.EnumerateObject())
                    d[p.Name] = FromJsonElement(p.Value);
                return d;

            default:
                return t.GetString()!;
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
        try { _unixListener?.Dispose(); } catch { }
        _unixListener = null;
        if (!string.IsNullOrEmpty(_unixSocketPath))
        {
            try
            {
                if (File.Exists(_unixSocketPath))
                    File.Delete(_unixSocketPath);
            }
            catch { }
            _unixSocketPath = null;
        }
        Interlocked.Exchange(ref _bridgeStream, null)?.Dispose();
        FailPending();
    }
}
