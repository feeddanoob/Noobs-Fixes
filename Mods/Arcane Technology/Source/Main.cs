using HarmonyLib;
using Verse;

namespace ArcaneTechnologyAnomalyPatch
{
    // Combined into one StaticConstructorOnStartup class (rather than
    // splitting Harmony-instance-creation into its own lazily-triggered
    // static constructor like Noob's Fixes.HarmonyPatches does) so this is
    // guaranteed to run - RimWorld scans for [StaticConstructorOnStartup]
    // types directly, so this doesn't depend on something else touching the
    // class first.
    [StaticConstructorOnStartup]
    internal static class Main
    {
        // Distinct from the main "com.feeddanoob.NoobEdit" instance used in
        // Noob's Fixes.dll - this is a separate assembly (only loaded at all
        // when Arcane Technology is active, via LoadFolders.xml), so it gets
        // its own Harmony instance.
        static Main()
        {
            var harmony = new Harmony("com.feeddanoob.NoobEdit.ArcaneTechnology");
            harmony.PatchAll();
            Log.Message("Noob's Arcane Technology Anomaly Patch Loaded");
        }
    }
}
