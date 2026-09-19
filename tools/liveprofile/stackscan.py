"""Stack-scan sampler: for each sample, read the target thread's stack memory from RSP to
the stack base and report every 8-byte word that is a return address into a native module
(named via PDB). Managed JIT frames are invisible to the walker, so this is how we see the
player-loop stage under them. Stale words can appear; the consistent ones are the real stack.
usage: stackscan.py <pid> <tid> [samples] [interval] [filter-regex]
"""
import ctypes, ctypes.wintypes as w, sys, time, collections, os, re, struct
sys.path.insert(0, os.path.dirname(__file__))
from ipsample import k32, modules, CONTEXT, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ, THREAD_GET_CONTEXT, THREAD_SUSPEND_RESUME
from ipsample2 import dbghelp, SYMBOL_INFOW, MAX_SYM_NAME, SYMOPT_UNDNAME, SYMOPT_DEFERRED_LOADS

CONTEXT_ALL = 0x10001F
THREAD_QUERY_INFORMATION = 0x40
ntdll = ctypes.WinDLL("ntdll")


class THREAD_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("ExitStatus", ctypes.c_long), ("TebBaseAddress", ctypes.c_void_p), ("UniqueProcess", ctypes.c_void_p),
                ("UniqueThread", ctypes.c_void_p), ("AffinityMask", ctypes.c_size_t), ("Priority", ctypes.c_long), ("BasePriority", ctypes.c_long)]


def main():
    pid, tid = int(sys.argv[1]), int(sys.argv[2])
    n_samples = int(sys.argv[3]) if len(sys.argv) > 3 else 100
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.03
    flt = re.compile(sys.argv[5]) if len(sys.argv) > 5 else None
    symdir = os.path.join(os.path.dirname(__file__), "symflat")

    k32.OpenProcess.restype = w.HANDLE; k32.OpenThread.restype = w.HANDLE
    k32.SuspendThread.argtypes = k32.ResumeThread.argtypes = [w.HANDLE]
    k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    hp = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION, False, tid)
    mods = modules(hp)
    modmap = {nm: (lo, hi) for lo, hi, nm in mods}

    # TEB -> stack base/limit
    tbi = THREAD_BASIC_INFORMATION()
    ntdll.NtQueryInformationThread(ht, 0, ctypes.byref(tbi), ctypes.sizeof(tbi), None)
    teb = ctypes.c_ulonglong * 3
    buf = teb()
    got = ctypes.c_size_t()
    k32.ReadProcessMemory(hp, tbi.TebBaseAddress, ctypes.byref(buf), 24, ctypes.byref(got))
    stack_base, stack_limit = buf[1], buf[2]

    dbghelp.SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS)
    dbghelp.SymInitializeW.argtypes = [w.HANDLE, ctypes.c_wchar_p, w.BOOL]
    dbghelp.SymInitializeW(hp, symdir, False)
    dbghelp.SymLoadModuleExW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_ulonglong, w.DWORD, ctypes.c_void_p, w.DWORD]
    dbghelp.SymLoadModuleExW.restype = ctypes.c_ulonglong
    dbghelp.SymFromAddrW.argtypes = [w.HANDLE, ctypes.c_ulonglong, ctypes.POINTER(ctypes.c_ulonglong), ctypes.POINTER(SYMBOL_INFOW)]
    psapi = ctypes.WinDLL("psapi")
    psapi.GetModuleFileNameExW.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, w.DWORD]
    psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(ctypes.c_void_p), w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
    arr = (ctypes.c_void_p * 2048)(); n = w.DWORD(); pbuf = ctypes.create_unicode_buffer(1024)
    psapi.EnumProcessModulesEx(hp, arr, ctypes.sizeof(arr), ctypes.byref(n), 3)
    paths = {}
    for i in range(n.value // 8):
        psapi.GetModuleFileNameExW(hp, arr[i], pbuf, 1024)
        paths[os.path.basename(pbuf.value)] = pbuf.value
    for lo, hi, nm in mods:
        if nm in ("UnityPlayer.dll", "mono-2.0-bdwgc.dll", "ntdll.dll"):
            dbghelp.SymLoadModuleExW(hp, None, paths.get(nm, nm), None, lo, hi - lo, None, 0)
    si = SYMBOL_INFOW()
    cache = {}

    def name(addr):
        if addr in cache:
            return cache[addr]
        out = None
        for lo, hi, nm in mods:
            if lo <= addr < hi:
                si.SizeOfStruct = 88; si.MaxNameLen = MAX_SYM_NAME
                disp = ctypes.c_ulonglong()
                if dbghelp.SymFromAddrW(hp, addr, ctypes.byref(disp), ctypes.byref(si)):
                    out = f"{nm}!{si.Name}"
                else:
                    out = f"{nm}+0x{addr-lo:x}"
                break
        cache[addr] = out
        return out

    ulo, uhi = modmap["UnityPlayer.dll"]
    mlo, mhi = modmap["mono-2.0-bdwgc.dll"]
    ctx = CONTEXT()
    results = []
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_ALL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        try:
            k32.GetThreadContext(ht, ctypes.byref(ctx))
            rsp = ctx.Rsp & ~7
            size = min(stack_base - rsp, 512 * 1024)
            sbuf = ctypes.create_string_buffer(size)
            k32.ReadProcessMemory(hp, rsp, sbuf, size, ctypes.byref(got))
            data = sbuf.raw[:got.value]
        finally:
            k32.ResumeThread(ht)
        leaf = ctx.Rip
        words = struct.unpack_from(f"<{len(data)//8}Q", data)
        chain = []
        for wd in words:
            if (ulo <= wd < uhi) or (mlo <= wd < mhi):
                nm = name(wd)
                if nm and (flt is None or flt.search(nm)):
                    if not chain or chain[-1] != nm:
                        chain.append(nm)
        results.append((name(leaf) or "<jit>", tuple(chain)))
        time.sleep(interval)

    tot = len(results)
    leafc = collections.Counter(r[0] for r in results)
    print(f"{tot} samples; stack {stack_limit:#x}..{stack_base:#x}")
    print("leaf:")
    for nm, c in leafc.most_common(8):
        print(f"{100.0*c/tot:6.1f}%  {nm}")
    incl = collections.Counter()
    for _, ch in results:
        for f in set(ch):
            incl[f] += 1
    print("\nreturn-address presence on the stack (filtered):")
    for f, c in incl.most_common(40):
        print(f"{100.0*c/tot:6.1f}%  {f}")
    # co-occurrence with the hot leaf
    if os.environ.get("CHAINS"):
        shown = 0
        for lf, ch in results:
            if "GetComponentsForListInternal" in " ".join(ch) and shown < int(os.environ["CHAINS"]):
                shown += 1
                print("\nCHAIN leaf=" + lf)
                for f in ch:
                    print("   ", f)
    hot = [ch for lf, ch in results if "GetComponentsForListInternal" in " ".join(ch) or "FillScriptingArray" in lf]
    if hot:
        inc2 = collections.Counter()
        for ch in hot:
            for f in set(ch):
                inc2[f] += 1
        print(f"\nwithin the {len(hot)} layout-rebuild samples, stack presence:")
        for f, c in inc2.most_common(30):
            print(f"{100.0*c/len(hot):6.1f}%  {f}")


if __name__ == "__main__":
    main()
