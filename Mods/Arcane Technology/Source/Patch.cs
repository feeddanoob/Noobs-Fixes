using System.Reflection;
using DArcaneTechnology;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ArcaneTechnologyAnomalyPatch
{
    [StaticConstructorOnStartup]
    public static class ArcaneTechnologyAnomalyPatchMod
    {
        static ArcaneTechnologyAnomalyPatchMod()
        {
            var harmony = new Harmony("feeddanoob.NoobEdit.ATanomalypatch");
            harmony.PatchAll();
            Log.Message("[ArcaneTechnology.AnomalyPatch] Loaded. Postfixing DArcaneTechnology.Base.Locked " +
                        "so research projects with no techLevel set (Anomaly's Dark Study tree, generally) " +
                        "can actually be locked - InLockedTechRange can never catch them on its own.");
        }
    }

    /// <summary>
    /// Root cause, confirmed directly from Arcane Technology's decompiled
    /// source (DArcaneTechnology.Base.cs):
    ///
    ///   public static bool Locked(ResearchProjectDef rpd)
    ///   {
    ///       if (rpd != null && DefDatabase&lt;ResearchProjectDef&gt;.AllDefs.Contains(rpd)
    ///           &amp;&amp; !GearAssigner.ProjectIsExempt(rpd)
    ///           &amp;&amp; InLockedTechRange(rpd)
    ///           &amp;&amp; (!rpd.IsFinished || ArcaneTechnologySettings.evenResearched))
    ///           return true;
    ///       return false;
    ///   }
    ///
    ///   public static bool InLockedTechRange(ResearchProjectDef rpd)
    ///   {
    ///       if (ArcaneTechnologySettings.restrictOnTechLevel)
    ///           return (int)rpd.techLevel > (int)(playerTechLevel + howManyTechLevelsAheadOfYours);
    ///       return (int)rpd.techLevel >= (int)ArcaneTechnologySettings.minToRestrict;
    ///   }
    ///
    /// TechLevel.Undefined is 0. minToRestrict's slider is clamped to
    /// Animal(1)..Archotech(7), so "techLevel >= minToRestrict" can never be
    /// true for an Undefined project under the default mode. In
    /// restrictOnTechLevel mode, playerTechLevel is realistically always
    /// >= 1 too, so "0 > playerTechLevel + ahead" can never be true either.
    /// Either way, InLockedTechRange(rpd) is always false for an Undefined
    /// project, so Locked(rpd) is always false, regardless of every other
    /// setting - including whether the research is actually finished.
    ///
    /// Every one of Arcane Technology's own patches (equip, wear, armor
    /// racks, sidearms, dual wield, wealth calc, outfit filters) calls
    /// Base.IsResearchLocked(ThingDef, Pawn), which calls this exact
    /// Locked(rpd) method - so patching Locked once covers all of them.
    /// </summary>
    [HarmonyPatch(typeof(Base), nameof(Base.Locked))]
    public static class Patch_Base_Locked_Postfix
    {
        // ArcaneTechnologySettings is an internal type in Arcane Technology's
        // assembly, so it can't be referenced directly (typeof/using would
        // fail to compile) - read its evenResearched field via reflection
        // instead, purely to mirror Locked()'s own behavior exactly. Falls
        // back to "false" (the default) if the field can't be found, e.g.
        // after an Arcane Technology update renames it.
        private static readonly FieldInfo evenResearchedField =
            AccessTools.Field(AccessTools.TypeByName("DArcaneTechnology.ArcaneTechnologySettings"), "evenResearched");

        public static void Postfix(ResearchProjectDef rpd, ref bool __result)
        {
            if (__result) return;   // Arcane Technology already locked it - nothing to add
            if (rpd == null) return;
            if (rpd.techLevel != TechLevel.Undefined) return; // has a real tier - Arcane Technology's own check already handles this correctly

            bool evenResearched = evenResearchedField != null && evenResearchedField.GetValue(null) is bool b && b;
            if (rpd.IsFinished && !evenResearched) return; // actually researched, and not forcing a lock anyway

            if (GearAssigner.ProjectIsExempt(rpd)) return; // respect Arcane Technology's own exemption list (clothing, etc.)

            __result = true;
        }
    }
}
