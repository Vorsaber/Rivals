"""Search a process's committed private memory for an ASCII needle. usage: memgrep.py <pid> <needle> [max_hits]"""
import ctypes, ctypes.wintypes as w, sys, time, re

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", w.DWORD),
                ("PartitionId", w.WORD), ("RegionSize", ctypes.c_size_t), ("State", w.DWORD), ("Protect", w.DWORD), ("Type", w.DWORD)]


def main():
    pid = int(sys.argv[1]); needle = sys.argv[2].encode(); max_hits = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    k32.OpenProcess.restype = w.HANDLE
    k32.VirtualQueryEx.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
    k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    hp = k32.OpenProcess(0x0400 | 0x0010, False, pid)
    addr = 0; mbi = MBI(); got = ctypes.c_size_t()
    hits = 0; scanned = 0; t0 = time.time()
    pat = re.compile(re.escape(needle) + rb"[\x20-\x7e]{0,60}")
    while addr < 0x7FFFFFFFFFFF and hits < max_hits:
        if not k32.VirtualQueryEx(hp, addr, ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0; size = mbi.RegionSize
        if mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) and mbi.Type == 0x20000 and size <= 1 << 31:
            off = 0
            while off < size and hits < max_hits:
                chunk = min(size - off, 64 << 20)
                buf = ctypes.create_string_buffer(chunk)
                if k32.ReadProcessMemory(hp, base + off, buf, chunk, ctypes.byref(got)) and got.value:
                    data = buf.raw[:got.value]
                    scanned += len(data)
                    for m in pat.finditer(data):
                        hits += 1
                        print(f"0x{base+off+m.start():x}: {m.group().decode('ascii', 'replace')}")
                        if hits >= max_hits:
                            break
                off += chunk
        addr = base + size
    print(f"scanned {scanned/1e9:.1f} GB in {time.time()-t0:.0f}s, {hits} hits")


if __name__ == "__main__":
    main()
