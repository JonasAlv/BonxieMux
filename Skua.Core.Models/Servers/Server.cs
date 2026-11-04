using Newtonsoft.Json;
using System.ComponentModel;
using Skua.Core.Models.Converters;

namespace Skua.Core.Models.Servers;

/// <summary>
/// A class holding information about a game server.
/// </summary>
public class Server : INotifyPropertyChanged
{
    /// <summary>
    /// The name of the game server.
    /// </summary>
    [JsonProperty("sName")]
    public string Name { get; set; }

    /// <summary>
    /// The IP address of the game server.
    /// </summary>
    [JsonProperty("sIP")]
    public string IP { get; set; }

    /// <summary>
    /// The chat level of this server (canned = 0, free = 2).
    /// </summary>
    [JsonProperty("iChat")]
    public int ChatLevel { get; set; }

    /// <summary>
    /// The port this server listens on.
    /// </summary>
    [JsonProperty("iPort")]
    public int Port { get; set; }

    /// <summary>
    /// Indicates whether the server is online.
    /// </summary>
    [JsonProperty("bOnline")]
    [JsonConverter(typeof(StringBoolConverter))]
    public bool Online { get; set; }

    /// <summary>
    /// The language of this server (en/pt).
    /// </summary>
    [JsonProperty("sLang")]
    public string Lang { get; set; }

    /// <summary>
    /// The number of players currently on the server.
    /// </summary>
    [JsonProperty("iCount")]
    public int PlayerCount { get; set; }

    /// <summary>
    /// Indicates whether this is an upgrade only server.
    /// </summary>
    [JsonProperty("bUpg")]
    [JsonConverter(typeof(StringBoolConverter))]
    public bool Upgrade { get; set; }

    [JsonProperty("iMax")]
    public int MaxPlayers { get; set; }

    [JsonProperty("iLevel")]
    public int Level { get; set; }

    /// <summary>
    /// The ping/latency to this server in milliseconds (calculated client-side).
    /// </summary>
    [JsonIgnore]
    public long Ping
    {
        get => _ping;
        set
        {
            if (_ping == value)
                return;
            _ping = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ping)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PingText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PingLevel)));
        }
    }

    private long _ping = -1;

    /// <summary>Ping value meaning "the server did not answer".</summary>
    public const long NoAnswer = 9999;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The ping for display: "offline", "...", "no answer" or "123 ms".</summary>
    [JsonIgnore]
    public string PingText => !Online ? "offline" : _ping switch
    {
        -1 => "...",
        NoAnswer => "no answer",
        _ => $"{_ping} ms"
    };

    /// <summary>0 = not measured (or offline), 1 = quick (under 120 ms), 2 = middling (under 300 ms), 3 = slow or no answer.</summary>
    [JsonIgnore]
    public int PingLevel => !Online || _ping < 0 ? 0 : _ping switch
    {
        < 120 => 1,
        < 300 => 2,
        _ => 3
    };

    public override string ToString()
    {
        return $"{Name} - {PlayerCount}";
    }

    public static bool operator ==(Server s0, Server s1)
    {
        return s0?.Name == s1?.Name;
    }

    public static bool operator !=(Server s0, Server s1)
    {
        return s0?.Name != s1?.Name;
    }

    public override bool Equals(object? obj)
    {
        return obj is Server server && server.Name == Name;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Name, IP);
    }
}