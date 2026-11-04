namespace Skua.Core.Models.Scripts;

public enum ScriptSourceKind
{
    Repository,
    CustomFolder,
    CustomFile
}

public sealed record ScriptSource(string FullPath, ScriptSourceKind Kind, bool Exists)
{
    public bool IsExplicitFile => Kind == ScriptSourceKind.CustomFile;
}

public sealed record ScriptPathResolution(
    string? SelectedPath,
    IReadOnlyList<string> ExistingCandidates,
    string? Error = null)
{
    public bool IsAmbiguous => ExistingCandidates.Count > 1;
}
