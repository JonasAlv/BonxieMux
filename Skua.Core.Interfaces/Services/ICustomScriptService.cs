using Skua.Core.Models.Scripts;

namespace Skua.Core.Interfaces.Services;

public interface ICustomScriptService
{
    IReadOnlyList<string> GetSearchRoots();

    IReadOnlyList<ScriptSource> Discover();

    bool IsCustomPath(string path);

    void SetCustomFolder(string? path);

    void AddCustomFile(string path);

    void RemoveCustomFile(string path);
}
