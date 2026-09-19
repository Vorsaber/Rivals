"""Managed-object stack scan: which Mono objects (by class) are referenced from the target
thread's stack right now? A MonoObject's first word is its MonoVTable*, whose first word is
MonoClass*, which carries `name` / `name_space` C strings. We validate the layout by demanding
printable strings, so a wrong guess just yields nothing.
usage: objscan.py <pid> <tid> [samples] [interval] [bytes-of-stack] [only-when-leaf-regex]
"""
import ctypes, ctypes.wintypes as w, sys, time, collections, os, re, struct
sys.path.insert(0, os.path.dirname(__file__))
from ipsample import k32, modules, CONTEXT, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ, THREAD_GET_CONTEXT, THREAD_SUSPEND_RESUME
from ipsample2 import Symbolizer

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

    def rdstack(addr, n):
        # page by page up to the first unreadable page (the stack's guard page), so a wide span never yields nothing
        out = []
        while n > 0:
            step = min(n, 4096 - (addr & 4095))
            b = rd(addr, step)
            if not b:
                break
            out.append(b); addr += step; n -= step
        return b"".join(out)

    def rdq(addr):
        b = rd(addr, 8)
        return struct.unpack("<Q", b)[0] if b else None

    def rdstr(addr, n=64, allow_empty=False):
        b = rd(addr, n)
        if not b:
            return None
        s = b.split(b"\0")[0]
        if not s:
            return "" if allow_empty else None
        if len(s) > 60:
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

    # symbols: reuse ipsample2's Symbolizer. objscan's own dbghelp session resolved every UnityPlayer
    # address to the wrong name (2026-09-19; cause not chased), so the leaf filter never matched.
    symb = Symbolizer(symdir, mods, hp)

    def leafname(a):
        nm = symb.name(a)
        if nm:
            return nm.rsplit('+0x', 1)[0]
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
            # the game's own scripts live in the GLOBAL namespace (PlayCardSetUI, CSingleton...): ns == "" is valid
            nm, ns = rdstr(np_), rdstr(nsp, 64, allow_empty=True)
            if nm and ns is not None and nm[0].isalpha() or (nm and nm[0] in "<_"):
                out = (ns + "." if ns else "") + nm
                break
        klass_cache[kp] = out
        return out

    ctx = CONTEXT()
    per_class = collections.Counter()
    leaves = collections.Counter()
    per_class_samples = collections.Counter()
    used = 0
    for _ in range(n_samples):
        ctx.ContextFlags = CONTEXT_ALL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            raise OSError(ctypes.get_last_error())
        try:
            k32.GetThreadContext(ht, ctypes.byref(ctx))
            rsp = ctx.Rsp & ~7
            data = rdstack(rsp, span)
            regs = [ctx.Rbx, ctx.Rsi, ctx.Rdi, ctx.R12, ctx.R13, ctx.R14, ctx.R15, ctx.Rcx, ctx.Rdx, ctx.Rbp]
            lf = leafname(ctx.Rip)
            leaves[lf] += 1
            if only and not only.search(lf):
                # `continue` used to skip the sleep below: a tight suspend loop that pinned the thread on one Rip
                skip = True
            else:
                skip = False
        finally:
            k32.ResumeThread(ht)
        if skip:
            time.sleep(interval)
            continue
        used += 1
        # the class scan runs with the thread RESUMED (live heap words; a stale one is possible, consistent ones are real)
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
        time.sleep(interval)
    print("leaf functions seen:")
    for nm, c in leaves.most_common(8):
        print(f"{100.0*c/n_samples:6.1f}%  {nm}")
    print(f"{used} samples used (of {n_samples}); classes referenced from the stack (samples containing / total refs):")
    for nm, c in per_class_samples.most_common(70):
        print(f"{100.0*c/max(used,1):6.1f}%  {per_class[nm]:6d}  {nm}")


if __name__ == "__main__":
    main()
