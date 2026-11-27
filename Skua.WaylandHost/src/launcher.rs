use std::env;
use std::ffi::OsString;
use std::fs;
use std::os::unix::process::CommandExt;
use std::process::{exit, Command};

fn main() {
    let exe_path = match fs::read_link("/proc/self/exe") {
        Ok(p) => p,
        Err(e) => {
            eprintln!("bonxie-launcher: readlink /proc/self/exe failed: {e}");
            exit(1);
        }
    };

    let Some(dir) = exe_path.parent() else {
        eprintln!("bonxie-launcher: missing parent directory");
        exit(1);
    };
    let Some(exe_name) = exe_path.file_name() else {
        eprintln!("bonxie-launcher: missing executable name");
        exit(1);
    };

    let current_dir = dir.join("current");
    let target = current_dir.join(exe_name);

    if let Err(e) = env::set_current_dir(&current_dir) {
        eprintln!(
            "bonxie-launcher: chdir({}) failed: {e}",
            current_dir.display()
        );
        exit(1);
    }

    let mut ld_library_path = OsString::from(current_dir.as_os_str());
    ld_library_path.push(":");
    ld_library_path.push(current_dir.join("runtimes/linux-x64/native").as_os_str());
    if let Some(orig) = env::var_os("LD_LIBRARY_PATH") {
        if !orig.is_empty() {
            ld_library_path.push(":");
            ld_library_path.push(orig);
        }
    }

    let mut path_var = OsString::from(current_dir.as_os_str());
    if let Some(orig) = env::var_os("PATH") {
        if !orig.is_empty() {
            path_var.push(":");
            path_var.push(orig);
        }
    }

    let mut cmd = Command::new(&target);
    cmd.args(env::args_os().skip(1))
        .env("LD_LIBRARY_PATH", ld_library_path)
        .env("PATH", path_var)
        .env("DOTNET_ROOT", current_dir.as_os_str());
    if env::var_os("RUST_LOG").is_none() {
        cmd.env("RUST_LOG", "warn,ruffle_frontend_utils::backends::audio=off");
    }
    if env::var_os("DOTNET_EnableDiagnostics").is_none() {
        cmd.env("DOTNET_EnableDiagnostics", "0");
    }
    let err = cmd.exec();

    eprintln!("bonxie-launcher: exec({}) failed: {err}", target.display());
    exit(1);
}
