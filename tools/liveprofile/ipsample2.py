"""Leaf-IP sampler + dbghelp symbolizer for a foreign same-user process (no admin).

usage: ipsample2.py <pid> <tid> [samples] [interval_s] [symdir]
"""
import ctypes, ctypes.wintypes as w, sys, time, collections, os
sys.path.insert(0, os.path.dirname(__file__))
from ipsample import k32, modules, CONTEXT, CONTEXT_CONTROL, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ, THREAD_GET_CONTEXT, THREAD_SUSPEND_RESUME

dbghelp = ctypes.WinDLL("dbghelp", use_last_error=True)
SYMOPT_UNDNAME = 0x2
SYMOPT_DEFERRED_LOADS = 0x4
SYMOPT_LOAD_LINES = 0x10
MAX_SYM_NAME = 2000


class SYMBOL_INFOW(ctypes.Structure):
    _fields_ = [("SizeOfStruct", w.ULONG), ("TypeIndex", w.ULONG), ("Reserved", ctypes.c_ulonglong * 2),
                ("Index", w.ULONG), ("Size", w.ULONG), ("ModBase", ctypes.c_ulonglong), ("Flags", w.ULONG),
                ("Value", ctypes.c_ulonglong), ("Address", ctypes.c_ulonglong), ("Register", w.ULONG),
                ("Scope", w.ULONG), ("Tag", w.ULONG), ("NameLen", w.ULONG), ("MaxNameLen", w.ULONG),
                ("Name", ctypes.c_wchar * (MAX_SYM_NAME + 1))]


class Symbolizer:
    def __init__(self, symdir, mods, hp):
        self.h = ctypes.c_void_p(0x1234)  # any unique handle value when fInvade=False
        dbghelp.SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS)
        dbghelp.SymInitializeW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, w.BOOL]
        if not dbghelp.SymInitializeW(self.h, symdir, False):
            raise OSError(ctypes.get_last_error())
        dbghelp.SymLoadModuleExW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p,
                                             ctypes.c_ulonglong, w.DWORD, ctypes.c_void_p, w.DWORD]
        dbghelp.SymLoadModuleExW.restype = ctypes.c_ulonglong
        dbghelp.SymFromAddrW.argtypes = [ctypes.c_void_p, ctypes.c_ulonglong, ctypes.POINTER(ctypes.c_ulonglong), ctypes.POINTER(SYMBOL_INFOW)]
        # full paths for the modules we care about
        paths = {}
        buf = ctypes.create_unicode_buffer(1024)
        psapi = ctypes.WinDLL("psapi")
        psapi.GetModuleFileNameExW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, w.DWORD]
        arr = (ctypes.c_void_p * 2048)(); n = w.DWORD()
        psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(ctypes.c_void_p), w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
        psapi.EnumProcessModulesEx(hp, arr, ctypes.sizeof(arr), ctypes.byref(n), 3)
        for i in range(n.value // 8):
            psapi.GetModuleFileNameExW(hp, arr[i], buf, 1024)
            paths[os.path.basename(buf.value)] = buf.value
        self.loaded = set()
        for lo, hi, nm in mods:
            if nm in ("UnityPlayer.dll", "ntdll.dll", "mono-2.0-bdwgc.dll", "win32u.dll", "KERNELBASE.dll", "kernel32.dll"):
                r = dbghelp.SymLoadModuleExW(self.h, None, paths.get(nm, nm), None, lo, hi - lo, None, 0)
                if r:
                    self.loaded.add(nm)
        self.si = SYMBOL_INFOW()

    def name(self, addr):
        self.si.SizeOfStruct = 88
        self.si.MaxNameLen = MAX_SYM_NAME
        disp = ctypes.c_ulonglong()
        if dbghelp.SymFromAddrW(self.h, addr, ctypes.byref(disp), ctypes.byref(self.si)):
            return f"{self.si.Name}+0x{disp.value:x}"
        return None


def main():
    pid, tid = int(sys.argv[1]), int(sys.argv[2])
    n_samples = int(sys.argv[3]) if len(sys.argv) > 3 else 500
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.01
    symdir = sys.argv[5] if len(sys.argv) > 5 else os.path.join(os.path.dirname(__file__), "symflat")
    k32.OpenProcess.restype = w.HANDLE; k32.OpenThread.restype = w.HANDLE
    k32.SuspendThread.argtypes = k32.ResumeThread.argtypes = [w.HANDLE]
    hp = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, False, tid)
    if not hp or not ht:
        raise OSError(ctypes.get_last_error())
    mods = modules(hp)
    k32.GetThreadContext.argtypes = [w.HANDLE, ctypes.c_void_p]
    ctx = CONTEXT()
    raw = []
    t0 = time.perf_counter()
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_CONTROL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        try:
            ok = k32.GetThreadContext(ht, ctypes.byref(ctx))
        finally:
            k32.ResumeThread(ht)
        raw.append(ctx.Rip if ok else 0)
        time.sleep(interval)
    dt = time.perf_counter() - t0
    sym = Symbolizer(symdir, mods, hp)
    by_mod = collections.Counter(); by_fn = collections.Counter()
    for rip in raw:
        name = "<jit>"
        for lo, hi, nm in mods:
            if lo <= rip < hi:
                name = nm; break
        by_mod[name] += 1
        fn = sym.name(rip) if name in sym.loaded else None
        fn = fn.split("+0x")[0] if fn else (f"{name}+0x{rip - lo:x}" if name != "<jit>" else "<jit>")
        by_fn[(name, fn)] += 1
    tot = len(raw)
    print(f"{tot} samples of thread {tid} over {dt:.1f}s (symbols loaded: {sorted(sym.loaded)})")
    for nm, c in by_mod.most_common():
        print(f"{100.0*c/tot:6.1f}%  {nm}")
    print("top leaf functions:")
    for (nm, fn), c in by_fn.most_common(30):
        print(f"{100.0*c/tot:6.1f}%  {nm}!{fn}")


if __name__ == "__main__":
    main()
