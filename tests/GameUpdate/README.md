# Game update regression checks

Run on Windows with the updated game installed:

```powershell
dotnet run --project tests/GameUpdate/GameUpdate.csproj -c Release -- "<game directory>"
```

If needed, also pass `-p:GamePath="<game directory>"` to point the build at that installation.
The executable uses the real plugin and local game assemblies. It checks Ascension price
serialization, application to stale or missing rows, percentage checksums, the player
tournament icon and score on the wire, and guest/host/solo activity entry policies.
The standalone runner uses the signed upstream Newtonsoft.Json assembly because Unity's
delay-signed copy requires Mono. It does not launch the game or touch saves.

These checks do not simulate Unity interactions, transport, or two-player gameplay.
