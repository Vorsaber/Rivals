"""Managed-object stack scan: which Mono objects (by class) are referenced from the target
thread's stack right now? A MonoObject's first word is its MonoVTable*, whose first word is
MonoClass*, which carries `name` / `name_space` C strings. We validate the layout by demanding
printable strings, so a wrong guess just yields nothing.
usage: objscan.py <pid> <tid> [samples] [interval] [bytes-of-stack] [only-when-leaf-regex]
"""
import ctypes, ctypes.wintypes as w, sys, time, collections, os, re, struct
sys.path.insert(0, os.path.dirname(__file__))
from ipsample import k32, modules, CONTEXT, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ, THREAD_GET_CONTEXT, THREAD_SUSPEND_RESUME
from ipsample2 import dbghelp, SYMBOL_INFOW, MAX_SYM_NAME, SYMOPT_UNDNAME, SYMOPT_DEFERRED_LOADS

CONTEXT_ALL = 0x10001F
NAME_OFFSETS = [(0x40, 0x48), (0x48, 0x50), (0x38, 0x40), (0x50, 0x58)]


def main():
    pid, tid = int(sys.argv[1]), int(sys.argv[2])
    n_samples = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    interval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.05
    span = int(sys.argv[5]) if len(sys.argv) > 5 else 48 * 1024
    only = re.compile(sys.argv[6]) if len(sys.argv) > 6 else None
    symdir = os.path.join(os.path.dirname(__file__), "symflat")

    k32.OpenProcess.restype = w.HANDLE; k32.OpenThread.restype = w.HANDLE
    k32.SuspendThread.argtypes = k32.ResumeThread.argtypes = [w.HANDLE]
    k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    hp = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, False, tid)
    mods = modules(hp)
    got = ctypes.c_size_t()

    def rd(addr, n):
        b = ctypes.create_string_buffer(n)
        if k32.ReadProcessMemory(hp, addr, b, n, ctypes.byref(got)) and got.value == n:
            return b.raw
        return None

    def rdq(addr):
        b = rd(addr, 8)
        return struct.unpack("<Q", b)[0] if b else None

    def rdstr(addr, n=64):
        b = rd(addr, n)
        if not b:
            return None
        s = b.split(b"\0")[0]
        if not s or len(s) > 60:
            return None
        try:
            t = s.decode("ascii")
        except UnicodeDecodeError:
            return None
        if not re.fullmatch(r"[A-Za-z0-9_.`<>\[\],+@$-]+", t):
            return None
        return t

    def in_module(a):
        for lo, hi, nm in mods:
            if lo <= a < hi:
                return True
        return False

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
    for i in range(n.value // 8):
        psapi.GetModuleFileNameExW(hp, arr[i], pbuf, 1024)
        if os.path.basename(pbuf.value) == "UnityPlayer.dll":
            for lo, hi, nm in mods:
                if nm == "UnityPlayer.dll":
                    dbghelp.SymLoadModuleExW(hp, None, pbuf.value, None, lo, hi - lo, None, 0)
    si = SYMBOL_INFOW()

    def leafname(a):
        si.SizeOfStruct = 88; si.MaxNameLen = MAX_SYM_NAME
        disp = ctypes.c_ulonglong()
        if dbghelp.SymFromAddrW(hp, a, ctypes.byref(disp), ctypes.byref(si)):
            return si.Name
        return "<jit>" if not in_module(a) else "?"

    klass_cache = {}

    def klass_name(kp):
        if kp in klass_cache:
            return klass_cache[kp]
        out = None
        for on, ons in NAME_OFFSETS:
            np_, nsp = rdq(kp + on), rdq(kp + ons)
            if not np_ or not nsp:
                continue
            nm, ns = rdstr(np_), rdstr(nsp, 64)
            if nm and ns is not None and nm[0].isalpha() or (nm and nm[0] in "<_"):
                out = (ns + "." if ns else "") + nm
                break
        klass_cache[kp] = out
        return out

    ctx = CONTEXT()
    per_class = collections.Counter()
    per_class_samples = collections.Counter()
    used = 0
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_ALL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        try:
            k32.GetThreadContext(ht, ctypes.byref(ctx))
            rsp = ctx.Rsp & ~7
            data = rd(rsp, span) or b""
            regs = [ctx.Rbx, ctx.Rsi, ctx.Rdi, ctx.R12, ctx.R13, ctx.R14, ctx.R15, ctx.Rcx, ctx.Rdx, ctx.Rbp]
            lf = leafname(ctx.Rip)
            if only and not only.search(lf):
                continue
            used += 1
            words = list(regs) + list(struct.unpack_from(f"<{len(data)//8}Q", data))
            seen = set()
            for wd in words:
                if wd < 0x10000 or wd > 0x7FFFFFFFFFFF or (wd & 7) or in_module(wd):
                    continue
                vt = rdq(wd)
                if not vt or vt < 0x10000 or vt > 0x7FFFFFFFFFFF or (vt & 7):
                    continue
                kp = rdq(vt)
                if not kp or kp < 0x10000 or kp > 0x7FFFFFFFFFFF or (kp & 7):
                    continue
                # MonoVTable: klass at +0, then gc_descr, domain, type ... sanity: vtable->domain-ish pointers
                nm = klass_name(kp)
                if not nm:
                    continue
                per_class[nm] += 1
                seen.add(nm)
            for nm in seen:
                per_class_samples[nm] += 1
        finally:
            k32.ResumeThread(ht)
        time.sleep(interval)
    print(f"{used} samples used (of {n_samples}); classes referenced from the stack (samples containing / total refs):")
    for nm, c in per_class_samples.most_common(70):
        print(f"{100.0*c/max(used,1):6.1f}%  {per_class[nm]:6d}  {nm}")


if __name__ == "__main__":
    main()
