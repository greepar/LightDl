#!/usr/bin/env python3
"""Associate GNU COFF unwind COMDATs with their functions for the MSVC linker.

Only section-definition auxiliary metadata is changed. Machine code, relocation
records, unwind payloads, archive member sizes, and symbol indexes stay intact.
"""
import pathlib
import struct
import sys

for argument in sys.argv[1:]:
    path = pathlib.Path(argument)
    archive = bytearray(path.read_bytes())
    assert archive[:8] == b"!<arch>\n", path
    member = 8
    changes = 0
    while member < len(archive):
        assert archive[member + 58:member + 60] == b"`\n"
        size = int(archive[member + 48:member + 58])
        start = member + 60
        machine, count = struct.unpack_from("<HH", archive, start)
        if machine in (0x8664, 0xAA64):
            symbols, symbol_count, optional = struct.unpack_from("<IIH", archive, start + 8)
            strings = start + symbols + symbol_count * 18
            def string(offset):
                end = archive.index(0, strings + offset)
                return archive[strings + offset:end].decode("ascii")
            sections = {}
            for i in range(count):
                header = start + 20 + optional + i * 40
                raw = bytes(archive[header:header + 8]).rstrip(b"\0")
                name = string(int(raw[1:])) if raw.startswith(b"/") else raw.decode("ascii")
                flags = struct.unpack_from("<I", archive, header + 36)[0]
                sections[i + 1] = (name, flags)
            by_name = {name: i for i, (name, _) in sections.items()}
            index = 0
            while index < symbol_count:
                symbol = start + symbols + index * 18
                section, kind, storage, auxiliary = struct.unpack_from("<hHBB", archive, symbol + 12)
                if section > 0 and storage == 3 and kind == 0 and auxiliary:
                    name, flags = sections[section]
                    if flags & 0x1000 and (name.startswith(".xdata$") or name.startswith(".pdata$")):
                        target = by_name.get(".text$" + name.split("$", 1)[1])
                        if target and sections[target][1] & 0x1000:
                            aux = symbol + 18
                            struct.pack_into("<H", archive, aux + 12, target)
                            archive[aux + 14] = 5  # IMAGE_COMDAT_SELECT_ASSOCIATIVE
                            struct.pack_into("<H", archive, aux + 16, 0)
                            changes += 1
                index += 1 + auxiliary
        member = start + size + size % 2
    path.write_bytes(archive)
    print(f"{path}: associated {changes} unwind sections")
