using System;
using System.Collections.Generic;

namespace Skua.Core.Generators;

internal class Default
{
    public static readonly string DefaultString = "default";

    public static string Get(string? typeName)
    {
        if (typeName == null)
            return DefaultString;

        Type? type = Type.GetType(typeName);
        return type == null
            ? DefaultString
            : type.IsArray
            ? $"Array.Empty<{type.FullName}>()"
            : type == typeof(string) ? string.Empty : typeof(IEnumerable<>).IsAssignableFrom(type) ? "new()" : DefaultString;
    }
}