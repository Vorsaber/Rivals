"""Print the CodeView PDB name + GUID/age of a PE file (what a symbol server keys on)."""
import struct, sys, uuid


def pdb_id(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    assert d[pe:pe + 4] == b"PE\0\0"
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    dd_off = opt + (112 if magic == 0x20B else 96)
    dbg_rva, dbg_size = struct.unpack_from("<II", d, dd_off + 6 * 8)
    optsize = struct.unpack_from("<H", d, pe + 20)[0]
    secs = []
    for i in range(nsec):
        s = opt + optsize + i * 40
        vsize, va, rsize, raw = struct.unpack_from("<IIII", d, s + 8)
        secs.append((va, max(vsize, rsize), raw))

    def rva2off(r):
        for va, sz, raw in secs:
            if va <= r < va + sz:
                return raw + (r - va)
        raise ValueError(hex(r))

    off = rva2off(dbg_rva)
    for i in range(dbg_size // 28):
        e = off + i * 28
        typ, size, addr_rva, ptr = struct.unpack_from("<IIII", d, e + 12)
        if typ == 2:  # CODEVIEW
            cv = d[ptr:ptr + size]
            if cv[:4] == b"RSDS":
                g = uuid.UUID(bytes_le=cv[4:20])
                age = struct.unpack_from("<I", cv, 20)[0]
                name = cv[24:].split(b"\0")[0].decode()
                return name, f"{g.hex.upper()}{age:X}"
    return None


if __name__ == "__main__":
    for p in sys.argv[1:]:
        print(p, pdb_id(p))
