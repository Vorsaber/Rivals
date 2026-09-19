"""Leaf-IP sampler for a foreign same-user process (no admin, no injection).

Suspends the target thread for a few microseconds per sample, reads RIP, resumes, and
maps RIP to the owning module. Output: % of samples per module. JIT'd Mono code lands in
no module and is reported as <jit>.
"""
import ctypes, ctypes.wintypes as w, sys, time, collections

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
THREAD_GET_CONTEXT = 0x0008
THREAD_SUSPEND_RESUME = 0x0002
CONTEXT_AMD64 = 0x100000
CONTEXT_CONTROL = CONTEXT_AMD64 | 0x1
LIST_MODULES_ALL = 0x03


class M128A(ctypes.Structure):
    _fields_ = [("Low", ctypes.c_ulonglong), ("High", ctypes.c_longlong)]


class CONTEXT(ctypes.Structure):
    _align_ = 16
    _fields_ = [
        ("P1Home", ctypes.c_ulonglong), ("P2Home", ctypes.c_ulonglong), ("P3Home", ctypes.c_ulonglong),
        ("P4Home", ctypes.c_ulonglong), ("P5Home", ctypes.c_ulonglong), ("P6Home", ctypes.c_ulonglong),
        ("ContextFlags", w.DWORD), ("MxCsr", w.DWORD),
        ("SegCs", w.WORD), ("SegDs", w.WORD), ("SegEs", w.WORD), ("SegFs", w.WORD), ("SegGs", w.WORD), ("SegSs", w.WORD),
        ("EFlags", w.DWORD),
        ("Dr0", ctypes.c_ulonglong), ("Dr1", ctypes.c_ulonglong), ("Dr2", ctypes.c_ulonglong), ("Dr3", ctypes.c_ulonglong),
        ("Dr6", ctypes.c_ulonglong), ("Dr7", ctypes.c_ulonglong),
        ("Rax", ctypes.c_ulonglong), ("Rcx", ctypes.c_ulonglong), ("Rdx", ctypes.c_ulonglong), ("Rbx", ctypes.c_ulonglong),
        ("Rsp", ctypes.c_ulonglong), ("Rbp", ctypes.c_ulonglong), ("Rsi", ctypes.c_ulonglong), ("Rdi", ctypes.c_ulonglong),
        ("R8", ctypes.c_ulonglong), ("R9", ctypes.c_ulonglong), ("R10", ctypes.c_ulonglong), ("R11", ctypes.c_ulonglong),
        ("R12", ctypes.c_ulonglong), ("R13", ctypes.c_ulonglong), ("R14", ctypes.c_ulonglong), ("R15", ctypes.c_ulonglong),
        ("Rip", ctypes.c_ulonglong),
        ("FltSave", ctypes.c_byte * 512),
        ("VectorRegister", M128A * 26), ("VectorControl", ctypes.c_ulonglong),
        ("DebugControl", ctypes.c_ulonglong), ("LastBranchToRip", ctypes.c_ulonglong), ("LastBranchFromRip", ctypes.c_ulonglong),
        ("LastExceptionToRip", ctypes.c_ulonglong), ("LastExceptionFromRip", ctypes.c_ulonglong),
    ]


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", w.DWORD), ("EntryPoint", ctypes.c_void_p)]


def modules(hp):
    psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(ctypes.c_void_p), w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
    psapi.GetModuleBaseNameW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, w.DWORD]
    psapi.GetModuleInformation.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.POINTER(MODULEINFO), w.DWORD]
    n = w.DWORD()
    arr = (ctypes.c_void_p * 2048)()
    if not psapi.EnumProcessModulesEx(hp, arr, ctypes.sizeof(arr), ctypes.byref(n), LIST_MODULES_ALL):
        raise OSError(ctypes.get_last_error())
    out = []
    buf = ctypes.create_unicode_buffer(1024)
    mi = MODULEINFO()
    for i in range(n.value // ctypes.sizeof(ctypes.c_void_p)):
        h = arr[i]
        psapi.GetModuleBaseNameW(hp, h, buf, 1024)
        psapi.GetModuleInformation(hp, h, ctypes.byref(mi), ctypes.sizeof(mi))
        out.append((mi.lpBaseOfDll or 0, (mi.lpBaseOfDll or 0) + mi.SizeOfImage, buf.value))
    out.sort()
    return out


def main():
    pid, tid = int(sys.argv[1]), int(sys.argv[2])
    n_samples = int(sys.argv[3]) if len(sys.argv) > 3 else 300
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.02
    k32.OpenProcess.restype = w.HANDLE; k32.OpenThread.restype = w.HANDLE
    k32.SuspendThread.argtypes = k32.ResumeThread.argtypes = [w.HANDLE]
    hp = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, False, tid)
    if not hp or not ht:
        raise OSError(ctypes.get_last_error())
    mods = modules(hp)
    k32.GetThreadContext.argtypes = [w.HANDLE, ctypes.c_void_p]
    hits = collections.Counter()
    ips = collections.Counter()
    ctx = CONTEXT()
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_CONTROL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        try:
            ok = k32.GetThreadContext(ht, ctypes.byref(ctx))
        finally:
            k32.ResumeThread(ht)
        if not ok:
            hits["<ctx-fail>"] += 1
            continue
        rip = ctx.Rip
        name = "<jit>"
        for lo, hi, nm in mods:
            if lo <= rip < hi:
                name = nm
                break
        hits[name] += 1
        ips[(name, rip)] += 1
        time.sleep(interval)
    tot = sum(hits.values())
    print(f"{tot} samples of thread {tid} in pid {pid}")
    for nm, c in hits.most_common():
        print(f"{100.0*c/tot:6.1f}%  {nm}")
    print("top leaf IPs:")
    for (nm, rip), c in ips.most_common(12):
        base = next((lo for lo, hi, n2 in mods if n2 == nm), 0)
        print(f"{100.0*c/tot:6.1f}%  {nm}+0x{rip-base:x}" if base else f"{100.0*c/tot:6.1f}%  {nm} 0x{rip:x}")


if __name__ == "__main__":
    main()
