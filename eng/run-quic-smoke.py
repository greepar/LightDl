#!/usr/bin/env python3
"""Run smoke tests and report Windows loader failures before entering managed code."""
import pathlib
import platform
import subprocess
import sys

binary = pathlib.Path(sys.argv[1]).resolve()
if platform.system() == "Windows":
    import ctypes
    from pe_imports import read_imports
    if binary.suffix != ".exe":
        binary = pathlib.Path(str(binary) + ".exe")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.SetErrorMode(0x0001 | 0x0002)
    kernel.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel.GetProcAddress.restype = ctypes.c_void_p
    missing = []
    for module, functions in read_imports(binary).items():
        try:
            library = ctypes.WinDLL(module)
        except OSError as error:
            missing.append(f"{module}: {error}")
            continue
        for function in functions:
            name = ctypes.create_string_buffer(function.encode()) if isinstance(function, str) else None
            pointer = ctypes.cast(name, ctypes.c_void_p) if name is not None else ctypes.c_void_p(function)
            if not kernel.GetProcAddress(library._handle, pointer):
                missing.append(f"{module}!{function}")
    if missing:
        raise SystemExit("Unresolved Windows imports:\n" + "\n".join(missing))
    print("PASS: all Windows DLL imports resolve", flush=True)
result = subprocess.run([str(binary)], timeout=120)
if result.returncode:
    raise SystemExit(f"Native executable failed: exit={result.returncode} (0x{result.returncode & 0xffffffff:08X})")
