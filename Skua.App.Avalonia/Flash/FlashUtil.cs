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
    private TcpListener? _listener;
    private Process? _player;
    private int _acceptedPlayerPid;
    private StreamWriter? _bridge;
    private int _nextId;

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

    /// <summary>Native X11 window the player draws in as a child window; 0 gives it a window of its own.</summary>
    public nint ParentWindow { get; set; }

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
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--skua-bridge");
        start.ArgumentList.Add($"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        if (ParentWindow != 0)
        {
            start.ArgumentList.Add("--skua-parent");
            start.ArgumentList.Add(ParentWindow.ToString(CultureInfo.InvariantCulture));
            // On Linux the parent is an X11 window; the player would otherwise prefer Wayland, where it can't be embedded.
            start.Environment.Remove("WAYLAND_DISPLAY");
            start.Environment.Remove("WAYLAND_SOCKET");
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
            Interlocked.Exchange(ref _bridge, writer)?.Dispose();
            FailPending();

            try
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync() is { } line)
                    Receive(JObject.Parse(line));
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

    private void Receive(JObject msg)
    {
        if (msg["id"] is { } id)
        {
            if (_pending.TryRemove((int)id, out TaskCompletionSource<JObject>? tcs))
                tcs.TrySetResult(msg);
            return;
        }

        string function = (string)msg["ev"]!;
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

    public object Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            if (_bridge is not { } writer)
                return default!;

            int id = Interlocked.Increment(ref _nextId);
            TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                string request = JsonConvert.SerializeObject(new { id, name = function, args = args.Select(ToJs) });
                lock (_sendLock)
                    writer.WriteLine(request);

                if (!tcs.Task.Wait(CallTimeout))
                    throw new TimeoutException($"No answer from the game player within {CallTimeout.TotalSeconds}s.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }

            string? result = tcs.Task.Result["r"] switch
            {
                null or { Type: JTokenType.Null } => null,
                JValue { Value: bool b } => b ? "true" : "false",
                JValue v => Convert.ToString(v.Value, CultureInfo.InvariantCulture),
                JToken t => t.ToString(Formatting.None)
            };
            return result is null ? default! : Convert.ChangeType(result, type, CultureInfo.InvariantCulture);
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

    public void Dispose()
    {
        StopPlayer();
        _listener?.Stop();
        _listener = null;
        _bridge?.Dispose();
        FailPending();
    }
}
