"""Cross-process native stack sampler (dbghelp StackWalk64), no admin, no injection.

Walks the target thread's stack WHILE it is suspended (a few hundred microseconds per
sample), names native frames from local PDBs, shows managed (JIT) frames as <jit>.
usage: stacksample.py <pid> <tid> [samples] [interval_s] [maxdepth]
"""
import ctypes, ctypes.wintypes as w, sys, time, collections, os
sys.path.insert(0, os.path.dirname(__file__))
from ipsample import k32, modules, CONTEXT, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ, THREAD_GET_CONTEXT, THREAD_SUSPEND_RESUME
from ipsample2 import dbghelp, SYMBOL_INFOW, MAX_SYM_NAME, SYMOPT_UNDNAME, SYMOPT_DEFERRED_LOADS

CONTEXT_ALL = 0x10001F
IMAGE_FILE_MACHINE_AMD64 = 0x8664


class ADDRESS64(ctypes.Structure):
    _fields_ = [("Offset", ctypes.c_ulonglong), ("Segment", w.WORD), ("Mode", ctypes.c_int)]


class KDHELP64(ctypes.Structure):
    _fields_ = [("Thread", ctypes.c_ulonglong), ("ThCallbackStack", w.DWORD), ("ThCallbackBStore", w.DWORD),
                ("NextCallback", w.DWORD), ("FramePointer", w.DWORD), ("KiCallUserMode", ctypes.c_ulonglong),
                ("KeUserCallbackDispatcher", ctypes.c_ulonglong), ("SystemRangeStart", ctypes.c_ulonglong),
                ("KiUserExceptionDispatcher", ctypes.c_ulonglong), ("StackBase", ctypes.c_ulonglong),
                ("StackLimit", ctypes.c_ulonglong), ("BuildVersion", w.DWORD), ("RetpolineStubFunctionTableSize", w.DWORD),
                ("RetpolineStubFunctionTable", ctypes.c_ulonglong), ("RetpolineStubOffset", w.DWORD),
                ("RetpolineStubSize", w.DWORD), ("Reserved0", ctypes.c_ulonglong * 2)]


class STACKFRAME64(ctypes.Structure):
    _fields_ = [("AddrPC", ADDRESS64), ("AddrReturn", ADDRESS64), ("AddrFrame", ADDRESS64), ("AddrStack", ADDRESS64),
                ("AddrBStore", ADDRESS64), ("FuncTableEntry", ctypes.c_void_p), ("Params", ctypes.c_ulonglong * 4),
                ("Far", w.BOOL), ("Virtual", w.BOOL), ("Reserved", ctypes.c_ulonglong * 3), ("KdHelp", KDHELP64)]


READ_ROUTINE = ctypes.WINFUNCTYPE(w.BOOL, w.HANDLE, ctypes.c_ulonglong, ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD))
FTA_ROUTINE = ctypes.WINFUNCTYPE(ctypes.c_void_p, w.HANDLE, ctypes.c_ulonglong)
GMB_ROUTINE = ctypes.WINFUNCTYPE(ctypes.c_ulonglong, w.HANDLE, ctypes.c_ulonglong)


def main():
    pid, tid = int(sys.argv[1]), int(sys.argv[2])
    n_samples = int(sys.argv[3]) if len(sys.argv) > 3 else 200
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.02
    maxdepth = int(sys.argv[5]) if len(sys.argv) > 5 else 48
    symdir = os.path.join(os.path.dirname(__file__), "symflat")

    k32.OpenProcess.restype = w.HANDLE; k32.OpenThread.restype = w.HANDLE
    k32.SuspendThread.argtypes = k32.ResumeThread.argtypes = [w.HANDLE]
    k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    hp = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, False, tid)
    if not hp or not ht:
        raise OSError(ctypes.get_last_error())
    mods = modules(hp)

    dbghelp.SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS)
    dbghelp.SymInitializeW.argtypes = [w.HANDLE, ctypes.c_wchar_p, w.BOOL]
    if not dbghelp.SymInitializeW(hp, symdir, False):
        raise OSError(ctypes.get_last_error())
    dbghelp.SymLoadModuleExW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_ulonglong, w.DWORD, ctypes.c_void_p, w.DWORD]
    dbghelp.SymLoadModuleExW.restype = ctypes.c_ulonglong
    dbghelp.SymFromAddrW.argtypes = [w.HANDLE, ctypes.c_ulonglong, ctypes.POINTER(ctypes.c_ulonglong), ctypes.POINTER(SYMBOL_INFOW)]
    dbghelp.SymFunctionTableAccess64.argtypes = [w.HANDLE, ctypes.c_ulonglong]
    dbghelp.SymFunctionTableAccess64.restype = ctypes.c_void_p
    dbghelp.SymGetModuleBase64.argtypes = [w.HANDLE, ctypes.c_ulonglong]
    dbghelp.SymGetModuleBase64.restype = ctypes.c_ulonglong
    dbghelp.StackWalk64.argtypes = [w.DWORD, w.HANDLE, w.HANDLE, ctypes.POINTER(STACKFRAME64), ctypes.c_void_p,
                                    READ_ROUTINE, FTA_ROUTINE, GMB_ROUTINE, ctypes.c_void_p]

    psapi = ctypes.WinDLL("psapi")
    psapi.GetModuleFileNameExW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, w.DWORD]
    psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(ctypes.c_void_p), w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
    arr = (ctypes.c_void_p * 2048)(); n = w.DWORD(); buf = ctypes.create_unicode_buffer(1024)
    psapi.EnumProcessModulesEx(hp, arr, ctypes.sizeof(arr), ctypes.byref(n), 3)
    paths = {}
    for i in range(n.value // 8):
        psapi.GetModuleFileNameExW(hp, arr[i], buf, 1024)
        paths[os.path.basename(buf.value)] = buf.value
    for lo, hi, nm in mods:
        dbghelp.SymLoadModuleExW(hp, None, paths.get(nm, nm), None, lo, hi - lo, None, 0)

    @READ_ROUTINE
    def read_mem(h, addr, out, size, nread):
        got = ctypes.c_size_t()
        ok = k32.ReadProcessMemory(hp, addr, out, size, ctypes.byref(got))
        nread[0] = got.value
        return 1 if ok else 0

    fta = FTA_ROUTINE(lambda h, a: dbghelp.SymFunctionTableAccess64(h, a) or None)
    gmb = GMB_ROUTINE(lambda h, a: dbghelp.SymGetModuleBase64(h, a))

    si = SYMBOL_INFOW()
    cache = {}

    def name(addr):
        key = addr
        if key in cache:
            return cache[key]
        modname = "<jit>"
        for lo, hi, nm in mods:
            if lo <= addr < hi:
                modname = nm; break
        out = "<jit>"
        if modname != "<jit>":
            si.SizeOfStruct = 88; si.MaxNameLen = MAX_SYM_NAME
            disp = ctypes.c_ulonglong()
            if dbghelp.SymFromAddrW(hp, addr, ctypes.byref(disp), ctypes.byref(si)):
                out = f"{modname}!{si.Name}"
            else:
                out = f"{modname}+0x{addr - lo:x}"
        cache[key] = out
        return out

    ctx = CONTEXT()
    stacks = []
    t0 = time.perf_counter()
    walk_time = 0.0
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_ALL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        tw = time.perf_counter()
        try:
            if not k32.GetThreadContext(ht, ctypes.byref(ctx)):
                continue
            fr = STACKFRAME64()
            fr.AddrPC.Offset = ctx.Rip; fr.AddrPC.Mode = 3
            fr.AddrFrame.Offset = ctx.Rbp; fr.AddrFrame.Mode = 3
            fr.AddrStack.Offset = ctx.Rsp; fr.AddrStack.Mode = 3
            frames = []
            for _d in range(maxdepth):
                if not dbghelp.StackWalk64(IMAGE_FILE_MACHINE_AMD64, hp, ht, ctypes.byref(fr), ctypes.byref(ctx), read_mem, fta, gmb, None):
                    break
                if fr.AddrPC.Offset == 0:
                    break
                frames.append(fr.AddrPC.Offset)
        finally:
            k32.ResumeThread(ht)
        walk_time += time.perf_counter() - tw
        stacks.append(frames)
        time.sleep(interval)
    dt = time.perf_counter() - t0

    named = [[name(a) for a in fr] for fr in stacks]
    tot = len(named)
    print(f"{tot} stack samples of thread {tid} over {dt:.1f}s; thread held {walk_time*1000:.0f} ms total ({walk_time*1000/tot:.2f} ms/sample)")
    depth = collections.Counter(len(s) for s in named)
    print("depth histogram:", sorted(depth.items())[:12], "...")
    # inclusive counts: how often each function appears anywhere in a stack
    incl = collections.Counter()
    for s in named:
        for f in set(s):
            incl[f] += 1
    print("\ninclusive (function appears on the stack):")
    for f, c in incl.most_common(45):
        print(f"{100.0*c/tot:6.1f}%  {f}")
    # collapsed stacks, top ones
    print("\ntop collapsed stacks (leaf first, JIT frames collapsed):")
    coll = collections.Counter()
    for s in named:
        out = []
        for f in s:
            if out and out[-1] == f == "<jit>":
                continue
            out.append(f)
        coll[" <- ".join(out[:14])] += 1
    for s, c in coll.most_common(8):
        print(f"\n[{100.0*c/tot:5.1f}%] {s}")


if __name__ == "__main__":
    main()
