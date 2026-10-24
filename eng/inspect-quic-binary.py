#!/usr/bin/env python3
"""Check static MsQuic deployment without requiring a platform-specific Python module."""
import pathlib
import platform
import re
import struct
import subprocess
import sys

binary = pathlib.Path(sys.argv[1]).resolve()
if platform.system() == "Windows":
    binary = binary.with_suffix(".exe")
assert binary.is_file(), binary
assert not any("msquic" in p.name.lower() for p in binary.parent.iterdir()), "MsQuic sidecar present"
if platform.system() == "Darwin":
    text = subprocess.check_output(["otool", "-L", str(binary)], text=True)
    assert "msquic" not in text.lower() and "libssl" not in text.lower() and "libcrypto" not in text.lower(), text
elif platform.system() == "Linux":
    text = subprocess.check_output(["readelf", "-d", str(binary)], text=True)
    dependencies = "\n".join(line for line in text.splitlines() if "NEEDED" in line)
    assert not re.search(r"msquic|libssl|libcrypto", dependencies, re.I), dependencies
    if "--fully-static" in sys.argv:
        assert not dependencies, dependencies
        headers = subprocess.check_output(["readelf", "-l", str(binary)], text=True)
        assert "INTERP" not in headers, headers
else:
    # Parse PE import descriptors directly, also on Windows ARM64 runners.
    data = binary.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[pe:pe + 4] == b"PE\0\0"
    section_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    directory = optional + (112 if struct.unpack_from("<H", data, optional)[0] == 0x20B else 96)
    import_rva = struct.unpack_from("<I", data, directory + 8)[0]

    def offset(rva):
        for i in range(section_count):
            section = optional + optional_size + 40 * i
            size, address, raw_size, raw_offset = struct.unpack_from("<IIII", data, section + 8)
            if address <= rva < address + max(size, raw_size):
                return raw_offset + rva - address
        raise AssertionError(f"Unmapped PE address: {rva:x}")

    names = []
    descriptor = offset(import_rva)
    while any(data[descriptor:descriptor + 20]):
        name = offset(struct.unpack_from("<I", data, descriptor + 12)[0])
        names.append(data[name:data.index(b"\0", name)].decode("ascii"))
        descriptor += 20
    text = "\n".join(names)
    assert not re.search(r"msquic|libssl|libcrypto", text, re.I), text
print(text)
print("PASS: native dependencies contain no shared MsQuic/OpenSSL library")
