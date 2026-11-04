using Skua.Core.Models.Scripts;

namespace Skua.Core.Interfaces.Services;

public interface IScriptPathResolver
{
    ScriptPathResolution Resolve(string? ownerFile, string specifier);
}
