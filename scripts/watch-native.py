"""Poll mounted sources, keeping the last successful build on compiler errors."""
import pathlib
import subprocess
import time

root = pathlib.Path("/workspace/native")


def snapshot():
    return tuple(
        (str(path), path.stat().st_mtime_ns, path.stat().st_size)
        for path in sorted(root.rglob("*")) if path.is_file()
    )


previous = None
while True:
    current = snapshot()
    if current != previous:
        previous = current
        result = subprocess.run(["sh", "/workspace/scripts/build-native.sh"])
        if result.returncode:
            print("Native build failed. Fix the source; watching for changes.", flush=True)
        else:
            print("Native build ready. Reload the browser to load the new module.", flush=True)
    time.sleep(1)
