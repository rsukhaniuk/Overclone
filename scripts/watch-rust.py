"""Poll mounted Rust sources; build outputs and Cargo caches stay in volumes."""
import pathlib
import subprocess
import time

root = pathlib.Path("/workspace/rust")


def snapshot():
    paths = [root / "Cargo.toml", root / "Cargo.lock"]
    paths.extend((root / "src").rglob("*"))
    paths.extend((root / "tests").rglob("*"))
    paths.extend((root / ".cargo").rglob("*"))
    paths.extend([root / "build.rs", pathlib.Path("/workspace/scripts/build-rust.sh")])
    return tuple(
        (str(path), path.stat().st_mtime_ns, path.stat().st_size)
        for path in sorted(paths) if path.is_file()
    )


previous = None
while True:
    current = snapshot()
    if current != previous:
        previous = current
        result = subprocess.run(["sh", "/workspace/scripts/build-rust.sh"])
        if result.returncode:
            print("Rust build failed. Fix the source; watching for changes.", flush=True)
        else:
            print("Rust build ready. Reload the browser to load the new module.", flush=True)
    time.sleep(1)
