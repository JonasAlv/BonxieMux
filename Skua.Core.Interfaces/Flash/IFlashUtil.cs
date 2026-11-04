using Newtonsoft.Json;
using System.Text;
using System.Xml.Linq;

namespace Skua.Core.Interfaces;

/// <summary>
/// Represents the method that handles a call from a Flash object, providing the function name and its arguments.
/// </summary>
/// <param name="function">The name of the function being called from the Flash object. Cannot be null.</param>
/// <param name="args">An array of arguments passed to the function. May be empty if the function requires no arguments.</param>
public delegate void FlashCallHandler(string function, params object[] args);

/// <summary>
/// Defines an interface for interacting with Flash-based ActionScript objects, enabling calls to SWF functions, object
/// manipulation, and data retrieval or conversion between ActionScript and .NET types.
/// </summary>
/// <remarks>
/// Implementations of this interface provide methods to invoke Flash functions, access or modify
/// Flash objects by path, and convert data between Flash XML and .NET representations. The interface inherits from <see cref="IDisposable"/>, so implementations may hold
/// unmanaged resources that should be released when no longer needed.
/// </remarks>
public interface IFlashUtil : IDisposable
{
    event FlashCallHandler? FlashCall;

    /// <summary>
    /// Whether the world clip has been loaded yet.
    /// </summary>
    /// <remarks>This can be used as an additional way of checking if the player is logged in and ready to perform actions.</remarks>
    bool IsWorldLoaded => !IsNull("world");

    void InitializeFlash();

    /// <summary>
    /// Make a flash call for the specified <paramref name="function"/>.
    /// </summary>
    /// <param name="function">Function to be called in the SWF.</param>
    /// <param name="args">Parameters of the called <paramref name="function"/></param>
    /// <returns>A <see cref="string"/> representation of the return value.</returns>
    string? Call(string function, params object[] args);

    /// <summary>
    /// Make a flash call for the specified <paramref name="function"/>.
    /// </summary>
    /// <typeparam name="T"><see cref="Type"/> to convert the return value.</typeparam>
    /// <param name="function">Function to be called in the SWF.</param>
    /// <param name="args">Parameters of the called <paramref name="function"/></param>
    /// <returns>An object of the specified <typeparamref name="T"/> or <see langword="null"/></returns>
    T? Call<T>(string function, params object[] args);

    /// <summary>
    /// Make a flash call for the specified <paramref name="function"/>.
    /// </summary>
    /// <param name="function">Function to be called in the SWF.</param>
    /// <param name="type"><see cref="Type"/> that the return value will be converted to.</param>
    /// <param name="args">Parameters of the called <paramref name="function"/></param>
    /// <returns>An <see cref="object"/> of the specified <paramref name="type"/> or <see langword="null"/></returns>
    object? Call(string function, Type type, params object[] args);

    /// <summary>
    /// Converts a <see cref="XElement"/> to <see cref="object"/>.
    /// </summary>
    /// <param name="el">Element to parse.</param>
    /// <returns>An <see cref="object"/> representing the converted <see cref="XElement"/>.</returns>
    object FromFlashXml(XElement el);

    /// <summary>
    /// Creates a <see cref="IFlashObject{T}"/> from the specified <paramref name="path"/>.
    /// </summary>
    /// <typeparam name="T"><see cref="Type"/> of the flash object.</typeparam>
    /// <param name="path">Path to get the object from.</param>
    /// <returns></returns>
    IFlashObject<T> CreateFlashObject<T>(string path);

    /// <summary>
    /// Checks if the actionscript object at the given path is null.
    /// </summary>
    /// <param name="path">The path of the object to check.</param>
    /// <returns>True if the object at the given path is null (unset).</returns>
    bool IsNull(string path)
    {
        return Call<bool>("isNull", path);
    }

    /// <summary>
    /// Gets an actionscript object at the given location as a JSON string.
    /// </summary>
    /// <param name="path">The path of the object to get.</param>
    /// <returns>The value of the object at the given path as a serialzied JSON string.</returns>
    string? GetGameObject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.Contains('['))
        {
            int lastOpen = path.LastIndexOf('[');
            int lastClose = path.LastIndexOf(']');
            if (lastClose == path.Length - 1 && lastOpen >= 0 && lastClose > lastOpen && !path[..lastOpen].Contains('['))
            {
                string key = path[(lastOpen + 1)..lastClose].Trim('"', '\'');
                string finalPath = path[..lastOpen];
                return Call("getGameObjectKey", finalPath, key);
            }

            string normalized = NormalizeBracketPath(path);
            return Call("getGameObject", normalized);
        }
        return Call("getGameObject", path);
    }

    private static string NormalizeBracketPath(string path)
    {
        var sb = new StringBuilder(path.Length);
        bool inBracket = false;
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '[')
            {
                if (sb.Length > 0 && sb[^1] != '.')
                    sb.Append('.');
                inBracket = true;
            }
            else if (c == ']')
            {
                inBracket = false;
            }
            else if (inBracket && (c == '"' || c == '\''))
            {
                // skip quotes inside bracket
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Replace("..", ".");
    }

    private static readonly JsonSerializerSettings SafeJsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        Error = (sender, args) =>
        {
            System.Diagnostics.Trace.WriteLine($"[JSON Warning] {args.ErrorContext.Path}: {args.ErrorContext.Error.Message}");
            args.ErrorContext.Handled = true;
        }
    };

    /// <summary>
    /// Gets an actionscript object at the given location and deserializes it as JSON to the given type.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the object to.</typeparam>
    /// <param name="path">The path of the object to get (i.e. world.myAvatar.sta.$tha will get your haste stat).</param>
    /// <param name="def">The default value to return if the call/deserialization fails.</param>
    /// <returns>The deserialized value of the object at the given path.</returns>
    T? GetGameObject<T>(string path, T? def = default)
    {
        try
        {
            string? raw = GetGameObject(path);
            string? trimmed = raw?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "null" || trimmed == "undefined" || trimmed == "\"\"")
                return def;
            var result = JsonConvert.DeserializeObject<T>(trimmed, SafeJsonSettings);
            return result is null ? def : result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"GetGameObject<{typeof(T)}>('{path}') error: {ex.Message}");
            return def;
        }
    }

    /// <summary>
    /// Gets a static actionscript object at the given location as a JSON string.
    /// </summary>
    /// <param name="path">The path of the object to get.</param>
    /// <returns>The value of the object at the given path as a serialzied JSON string.</returns>
    string? GetGameObjectStatic(string path)
    {
        return Call("getGameObjectS", path);
    }

    /// <summary>
    /// Gets a static actionscript object at the given location and deserializes it as JSON to the given type.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the object to.</typeparam>
    /// <param name="path">The path of the object to get.</param>
    /// <param name="def">The default value to return if the call/deserialization fails.</param>
    /// <returns>The deserialized value of the object at the given path.</returns>
    T? GetGameObjectStatic<T>(string path, T? def = default)
    {
        try
        {
            string? raw = GetGameObjectStatic(path);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null" || raw == "undefined")
                return def;
            var result = JsonConvert.DeserializeObject<T>(raw, SafeJsonSettings);
            return result is null ? def : result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"GetGameObjectStatic<{typeof(T)}>('{path}') error: {ex.Message}");
            return def;
        }
    }

    /// <summary>
    /// Sets the value of the actionscript object at the given path.
    /// </summary>
    /// <param name="path">The path of the object to set.</param>
    /// <param name="value">The value to set the object to. This can be a string, any number type or a bool.</param>
    void SetGameObject(string path, object value)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (path.Contains('['))
        {
            int lastOpen = path.LastIndexOf('[');
            int lastClose = path.LastIndexOf(']');
            if (lastClose == path.Length - 1 && lastOpen >= 0 && lastClose > lastOpen && !path[..lastOpen].Contains('['))
            {
                string key = path[(lastOpen + 1)..lastClose].Trim('"', '\'');
                string finalPath = path[..lastOpen];
                Call("setGameObjectKey", finalPath, key, value);
                return;
            }

            string normalized = NormalizeBracketPath(path);
            Call("setGameObject", normalized, value);
            return;
        }
        Call("setGameObject", path, value);
    }

    /// <summary>
    /// Calls the actionscript object with the given name at the given location.
    /// </summary>
    /// <param name="path">The path to the object and its function name.</param>
    /// <param name="args">The arguments to pass to the function.</param>
    /// <returns>The value of the object returned by calling the function as a serialzied JSON string.</returns>
    string? CallGameFunction(string path, params object[] args)
    {
        return args.Length > 0 ? Call("callGameFunction", new object[] { path }.Concat(args).ToArray()) : Call("callGameFunction0", path);
    }

    /// <summary>
    /// Calls the actionscript object with the given name at the given location.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the return of the function as.</typeparam>
    /// <param name="path">The path to the object and its function name.</param>
    /// <param name="args">The arguments to pass to the function.</param>
    /// <returns>The deserialized value of the object returned by the function.</returns>
    T? CallGameFunction<T>(string path, params object[] args)
    {
        try
        {
            string? raw = CallGameFunction(path, args);
            if (string.IsNullOrWhiteSpace(raw))
                return default;
            return JsonConvert.DeserializeObject<T>(raw, SafeJsonSettings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CallGameFunction<{typeof(T)}>('{path}') error: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// Gets the actionscript object of the array at the given path at the given index in that array.
    /// </summary>
    /// <param name="path">The path to the array.</param>
    /// <param name="index">The index in the array to get the object from.</param>
    /// <returns>The value of the object at the given index in the array as a serialzied JSON string.</returns>
    string? GetArrayObject(string path, int index)
    {
        return Call("getArrayObject", path, index);
    }

    /// <summary>
    /// Gets the actionscript object of the array at the given path at the given index in that array.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the object in the array as.</typeparam>
    /// <param name="path">The path to the array.</param>
    /// <param name="index">The index in the array to get the object from.</param>
    /// <param name="def">The default value to return if the call/deserialization fails.</param>
    /// <returns>The deserialized value of the object at the given index in the array.</returns>
    T? GetArrayObject<T>(string path, int index, T? def = default)
    {
        try
        {
            string? raw = GetArrayObject(path, index);
            if (string.IsNullOrWhiteSpace(raw))
                return def;
            return JsonConvert.DeserializeObject<T>(raw, SafeJsonSettings) ?? def;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"GetArrayObject<{typeof(T)}>('{path}', {index}) error: {ex.Message}");
            return def;
        }
    }

    /// <summary>
    /// Selects the members of each object in the array at the given path and puts them into a new array and returns them.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize the contents of the array as.</typeparam>
    /// <param name="path">The path to the array.</param>
    /// <param name="selector">The name of the field to use to populate the new array.</param>
    /// <returns>A list of deserialized objects from the selected array.</returns>
    List<T> SelectArrayObjects<T>(string path, string selector)
    {
        try
        {
            string? raw = Call("selectArrayObjects", path, selector);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<T>();
            return JsonConvert.DeserializeObject<List<T>>(raw, SafeJsonSettings) ?? new List<T>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"SelectArrayObjects<{typeof(T)}>('{path}', '{selector}') error: {ex.Message}");
            return new List<T>();
        }
    }
}