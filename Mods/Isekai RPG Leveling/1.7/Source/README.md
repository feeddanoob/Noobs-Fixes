# Isekai RPG Leveling fixes (Noob's Edits)

Fixes for Isekai RPG Leveling (`JellyCreative.IsekaiLeveling`) applied as Harmony patches, so
Isekai itself stays the unmodified Steam Workshop copy. Loaded through `LoadFolders.xml` only when
Isekai is active.

## Layout

- `NoobsFixes.IsekaiLevelingFix/`: the mod assembly (builds into `..\Assemblies`).
  - `Core/`: setup, logging, conflict check, all-or-nothing patching.
  - `Fixes/`: one class per fix, numbered `#01`–`#26`; there is no #23 (each class comment says what it fixes and why).
  - `Shared/`: anchor list and IL fingerprinting, also compiled into the tool below.
  - `Core/KnownFingerprints.Generated.cs`: written by the tool, never by hand.
- `IsekaiFingerprintTool/`: offline helper (.NET 10 console app, not shipped).
- `..\Languages\English\Keyed\`: the two strings fix #17 adds.

## What the log tells you

A block tagged `[Noob's Edits: Isekai fixes]` is logged when RimWorld finishes loading
("Status at startup") and again every time a save is loaded or a new game starts ("Status at game
load"). Before each block every active fix is re-verified: each of its Harmony patches must still be
installed, and each of its code edits must be present in the method's final compiled code as read
back from Harmony after all mods' patches. The block's second line gives the counts. It lists every fix:

| Status | Meaning |
| --- | --- |
| applied | Patched and running. |
| built in | The loaded Isekai build already contains the change (the old local fork). Nothing patched. |
| not needed | Doesn't apply to this mod list (RimWorld of Magic / Medieval Overhaul not active). |
| PAUSED | Isekai changed a method the fix relies on since it was checked. Isekai's own code runs; the fix needs re-checking. |
| FAILED | Something it needs is missing, or Harmony refused. Nothing of it stays patched. |
| OFF (error) | It threw at runtime and switched itself off; Isekai's behaviour is back. The error is logged once. |
| OFF (gone) | The verification found part of it removed or overwritten (usually by another mod), so it switched itself off. |

A warning block also lists any other mod with a Harmony patch on an Isekai method these fixes
change or rely on, with the mod name, patch kind and what that kind can do. It runs at startup and
again on the first game load.

## After an Isekai update (fixes show PAUSED)

1. Dump the changed methods and regenerate the fingerprints:

   ```
   cd IsekaiFingerprintTool
   dotnet run -c Release -- --upstream "C:\Steam\steamapps\workshop\content\294100\3657580708\Assemblies\IsekaiLeveling.dll" --out "..\NoobsFixes.IsekaiLevelingFix\Core\KnownFingerprints.Generated.cs" --dump <some folder>
   ```

   Keep a dump from before the update to diff against: the `.upstream.txt` files are readable,
   normalized IL for every anchored method.
2. For each paused fix, read the new Isekai code. Decide whether the bug is still there and the patch
   still fits, whether upstream fixed it (then remove the fix), or whether the patch needs adapting.
3. `dotnet build -c Release` in `NoobsFixes.IsekaiLevelingFix` (with RimWorld closed, so the DLL can be replaced).

Only regenerate fingerprints after checking the fixes against the new code: regenerating is what
tells the mod "these fixes were verified against this build".

## Notes

- Isekai is compiled for .NET Framework 4.8, so this project targets `net48`.
- RimWorld 1.7 runs on a .NET 10 runtime that can inline small mod methods into their callers.
  A Harmony patch on an inlined method silently never runs. Fixes #02, #03, #12 and #14 therefore
  hook non-inlinable callers (large or virtual methods) as well as, or instead of, the small method.
- #26 is new (not from the fork): the Character Editor's "Auto-Distribute Points" button kept its own
  STR-first loop after the author fixed `AutoDistributeByClass`; #26 makes the button call the author's method.
- Not ported from the old fork, because Isekai's 2026-10-14 update fixed them itself: stat
  auto-distribution always giving single points to STR, and the stats window's effect list (cut-off
  rows, per-frame recomputation). Also not ported, because they have no effect at runtime: the
  skill-tree texture-loader de-duplication and the Medieval Overhaul compatibility log line.
