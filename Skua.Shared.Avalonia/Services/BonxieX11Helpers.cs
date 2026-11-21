using System.Runtime.InteropServices;

namespace Skua.Shared.Avalonia.Services;

/// <summary>
/// Consolidated native Linux X11 helpers backed by <c>libbonxie_x11.so</c> (<c>Skua.X11Host</c>).
/// </summary>
public static partial class BonxieX11Helpers
{
    private const string Lib = "bonxie_x11";

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_init_threads")]
    private static partial int NativeInitThreads();

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_translate_to_root")]
    private static unsafe partial int NativeTranslateToRoot(uint windowXid, int* outX, int* outY);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_query_pointer_root")]
    private static unsafe partial int NativeQueryPointerRoot(int* outX, int* outY);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_bell")]
    private static partial int NativeBell(int percent);

    public static int InitThreads() => NativeInitThreads();

    public static unsafe bool TranslateToRoot(uint windowXid, out int x, out int y)
    {
        int rx = 0;
        int ry = 0;
        bool ok = NativeTranslateToRoot(windowXid, &rx, &ry) != 0;
        x = rx;
        y = ry;
        return ok;
    }

    public static unsafe bool QueryPointerRoot(out int x, out int y)
    {
        int rx = 0;
        int ry = 0;
        bool ok = NativeQueryPointerRoot(&rx, &ry) != 0;
        x = rx;
        y = ry;
        return ok;
    }

    public static bool Bell(int percent) => NativeBell(percent) != 0;
}
