using System.Runtime.CompilerServices;

// The Rust client's script host (Vsr.SkuaHost) drives the shared rescue code (the Boss class / gear setup of a helper).
[assembly: InternalsVisibleTo("Vsr.SkuaHost")]
