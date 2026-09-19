# liveprofile -- read a running game's main thread without deploying anything

Cross-process samplers for the live `Card Shop Simulator.exe` (same user, no admin, no injection,
nothing loaded into the game). Written for fv-877 (2026-09-19) to find where solo-play frames went.
They suspend the main thread for microseconds per sample; the game keeps running.

Symbols: Unity publishes PDBs for release players. Once per game version:

    python tools/liveprofile/pdbid.py "<GamePath>/UnityPlayer.dll" C:/Windows/System32/ntdll.dll
    # -> ('...UnityPlayer_Win64_player_mono_x64.pdb', '<GUIDAGE>')
    mkdir tools/liveprofile/symflat
    curl -L -o tools/liveprofile/symflat/UnityPlayer_Win64_player_mono_x64.pdb       https://symbolserver.unity3d.com/UnityPlayer_Win64_player_mono_x64.pdb/<GUIDAGE>/UnityPlayer_Win64_player_mono_x64.pdb
    curl -L -o tools/liveprofile/symflat/ntdll.pdb https://msdl.microsoft.com/download/symbols/ntdll.pdb/<GUIDAGE>/ntdll.pdb

(`symflat/` is git-ignored: the Unity PDB is ~280 MB.)

Find the pid and the main thread (highest CPU thread):

    powershell -c "$p=Get-Process 'Card Shop Simulator'; $p.Id; ($p.Threads | sort TotalProcessorTime -desc)[0].Id"

Then, coarse to fine:

| script | answers | run |
|---|---|---|
| `ipsample2.py` | which native functions the main thread sits in (module + symbol, leaf only) | `python ipsample2.py <pid> <tid> 1000 0.01` |
| `stacksample.py` | the native call chain above the leaf (dbghelp StackWalk64; stops at the first managed frame) | `python stacksample.py <pid> <tid> 200 0.02` |
| `stackscan.py` | the player-loop stage UNDER the managed frames (stack-word scan; stale words possible, consistent ones are real); `CHAINS=3` prints full chains | `python stackscan.py <pid> <tid> 60 0.03 "Registrator\|CallUpdateMethod\|Canvas\|Layout"` |
| `objscan.py` | which MANAGED classes are referenced from the stack when the leaf matches a regex - the way to name a script when no managed symbols exist | `python objscan.py <pid> <tid> 2000 0.003 16384 "ActivateAwake\|GetComponentsForList"` |
| `memgrep.py` | grep the game's private memory for an ASCII string (e.g. `"(singleton)"` to see minted fake singletons) | `python memgrep.py <pid> "(singleton)"` |

What fv-877 read with them (05:49 session, 20 fps): ~55% of every frame was a uGUI layout rebuild
(`Component_CUSTOM_GetComponentsForListInternal` under `LayoutRebuilder`) whose stack was
`GameObject.SetActive(true)` -> `ActivateAwakeRecursively` -> `RectTransform.AwakeFromLoad` ->
`SendReapplyDrivenProperties`, 100% under `MonoBehaviour::CallUpdateMethod`. The managed script was not
named before the game closed; `objscan.py` during a laggy session is the next step, or the in-game
`[profile]` lines (`Diagnostics.PerfDebug = true`, Util/FrameProfiler.cs).
