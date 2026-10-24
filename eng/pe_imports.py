"""Read PE import names without requiring binutils or a third-party module."""
import struct

def read_imports(path):
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[pe:pe + 4] == b"PE\0\0"
    section_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    is64 = struct.unpack_from("<H", data, optional)[0] == 0x20B
    directory = optional + (112 if is64 else 96)
    import_rva = struct.unpack_from("<I", data, directory + 8)[0]
    def offset(rva):
        for i in range(section_count):
            section = optional + optional_size + 40 * i
            size, address, raw_size, raw_offset = struct.unpack_from("<IIII", data, section + 8)
            if address <= rva < address + max(size, raw_size):
                return raw_offset + rva - address
        raise AssertionError(f"Unmapped PE address: {rva:x}")
    def string(rva, skip=0):
        start = offset(rva) + skip
        return data[start:data.index(b"\0", start)].decode("ascii")
    result = {}
    descriptor = offset(import_rva)
    while any(data[descriptor:descriptor + 20]):
        lookup, _, _, name, address = struct.unpack_from("<IIIII", data, descriptor)
        functions = []
        thunk = offset(lookup or address)
        width, fmt = (8, "<Q") if is64 else (4, "<I")
        flag = 1 << (width * 8 - 1)
        while (value := struct.unpack_from(fmt, data, thunk)[0]):
            functions.append(value & 0xffff if value & flag else string(value, 2))
            thunk += width
        result[string(name)] = functions
        descriptor += 20
    return result
