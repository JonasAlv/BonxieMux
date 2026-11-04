namespace Skua.Core.Interfaces;

public interface IManagedWindow
{
    string Title { get; }
    int Width { get; }
    int Height { get; }
    bool CanResize { get; }
    int? X => null;
    int? Y => null;
}