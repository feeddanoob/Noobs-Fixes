using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DArcaneTechnology;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ArcaneTechnologyAnomalyPatch
{
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
    /// Confirmed against Anomaly's ResearchProjectDefs XML: none of its
    /// ~40 projects (BioferriteIgnition included) declare a techLevel.
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
        // fail to compile) - read its evenResearched field via reflection.
        // Base.Locked isn't just called on equip attempts - Arcane
        // Technology's own wealth-calculation transpiler calls it for every
        // item in the colony, so this can run often. Reflecting the field
        // on every single call is wasted work for a setting that almost
        // never changes mid-session, so it's cached here instead and only
        // refreshed when the settings window is actually saved (see
        // Patch_RefreshEvenResearchedCache below).
        private static readonly FieldInfo evenResearchedField =
            AccessTools.Field(AccessTools.TypeByName("DArcaneTechnology.ArcaneTechnologySettings"), "evenResearched");

        private static bool cachedEvenResearched;
        private static bool cacheLoaded;

        internal static void RefreshCache()
        {
            cachedEvenResearched = evenResearchedField != null && evenResearchedField.GetValue(null) is bool b && b;
            cacheLoaded = true;
        }

        public static void Postfix(ResearchProjectDef rpd, ref bool __result)
        {
            if (__result) return;   // Arcane Technology already locked it - nothing to add
            if (rpd == null) return;
            if (rpd.techLevel != TechLevel.Undefined) return; // has a real tier - Arcane Technology's own check already handles this correctly

            if (!cacheLoaded) RefreshCache(); // first call only, e.g. if settings were never opened this session
            if (rpd.IsFinished && !cachedEvenResearched) return; // actually researched, and not forcing a lock anyway

            if (GearAssigner.ProjectIsExempt(rpd)) return; // respect Arcane Technology's own exemption list (clothing, etc.)

            __result = true;
        }
    }

    /// <summary>
    /// Keeps Patch_Base_Locked_Postfix's cached copy of evenResearched in
    /// sync whenever the player actually changes and saves Arcane
    /// Technology's mod settings (ArcaneTechnologySettings.WriteAll is
    /// called from DoSettingsWindowContents -> WriteSettings). Uses a
    /// bare [HarmonyPatch] + TargetMethod() rather than
    /// typeof(ArcaneTechnologySettings) since that type is internal and
    /// can't be referenced at compile time.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_RefreshEvenResearchedCache
    {
        static MethodBase TargetMethod()
        {
            var settingsType = AccessTools.TypeByName("DArcaneTechnology.ArcaneTechnologySettings");
            return AccessTools.Method(settingsType, "WriteAll");
        }

        static void Postfix() => Patch_Base_Locked_Postfix.RefreshCache();
    }

    /// <summary>
    /// Handles "_Unique" weapon/apparel variants (Gun_HellcatRifle_Unique,
    /// Gun_ChargeLance_Unique, Gun_BeamRepeater_Unique, Bow_Great_Unique,
    /// etc - confirmed against Weapons_Unique.xml): every one of these
    /// explicitly nulls out its own recipeMaker
    /// (&lt;recipeMaker Inherit="False" IsNull="True" /&gt;), so
    /// Base.MakeDictionaries never finds a recipe for them and
    /// Base.thingDic never gets an entry - meaning Base.IsResearchLocked
    /// can never even look them up, regardless of what
    /// Patch_Base_Locked_Postfix above does. That's true whether the
    /// variant is Anomaly's (Gun_HellcatRifle_Unique) or Odyssey's
    /// (Gun_ChargeLance_Unique, Gun_BeamRepeater_Unique) or plain vanilla
    /// (Gun_Revolver_Unique) - same underlying gap either way.
    ///
    /// Every one of these variants does carry a &lt;descriptionHyperlinks&gt;
    /// entry pointing at the normal craftable ThingDef it's a variant of.
    /// This postfix runs right after Arcane Technology's own
    /// MakeDictionaries() (which itself runs HardAssign/OverrideAssign for
    /// manual overrides) and does the equivalent for any remaining
    /// weapon/apparel with no thingDic entry: follow its
    /// descriptionHyperlinks back to the ThingDef it's a variant of, and if
    /// that one already has a research requirement, inherit it. Everything
    /// downstream (equip, wear, armor racks, sidearms, wealth calc, outfit
    /// filters) picks this up for free since they all read thingDic through
    /// IsResearchLocked.
    ///
    /// A basic item with no research requirement at all (e.g.
    /// Bow_Great_Unique -> Bow_Great, a starting-tier neolithic bow) simply
    /// finds nothing to inherit and stays unrestricted, same as before.
    /// </summary>
    [HarmonyPatch(typeof(Base), nameof(Base.MakeDictionaries))]
    public static class Patch_Base_MakeDictionaries_Postfix
    {
        // CompProperties_DArcane is internal, so it can't be referenced by
        // type at compile time - constructed reflectively instead. This is
        // exactly the comp Arcane Technology attaches itself (in its own
        // MakeDictionaries loop and in HardAssign/OverrideAssign) so
        // CompDArcane.CompInspectStringExtra() can show
        // "Unknown technology (...)" on the item itself. Without it,
        // IsResearchLocked/Locked - and therefore equip-blocking - work
        // fine, since that goes through EquipmentUtility.CanEquip directly,
        // but nothing shows the reason on hover/inspect for these
        // recipe-less variants, because that string comes from the comp,
        // not from CanEquip.
        private static readonly System.Type compPropsType =
            AccessTools.TypeByName("DArcaneTechnology.CompProperties_DArcane");
        private static readonly ConstructorInfo compPropsCtor = compPropsType != null
            ? AccessTools.Constructor(compPropsType, new[] { typeof(ResearchProjectDef) })
            : null;

        public static void Postfix()
        {
            int added = 0;

            foreach (var thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (Base.thingDic.ContainsKey(thingDef)) continue; // already resolved via recipe or Arcane Technology's own overrides
                if (!thingDef.IsWeapon && !thingDef.IsApparel) continue;

                var rpd = ResolveViaHyperlink(thingDef, new HashSet<ThingDef>());
                if (rpd == null) continue;

                Base.thingDic[thingDef] = rpd;
                if (Base.researchDic.TryGetValue(rpd, out var list))
                {
                    if (!list.Contains(thingDef)) list.Add(thingDef);
                }
                else
                {
                    Base.researchDic[rpd] = new List<ThingDef> { thingDef };
                }

                AttachInspectStringComp(thingDef, rpd);

                added++;
            }

            Log.Message($"[ArcaneTechnologyAnomalyPatch] Linked {added} recipe-less weapon/apparel " +
                        "variant(s) to a research requirement via their descriptionHyperlinks, " +
                        "including the 'Unknown technology' inspect string comp.");
        }

        private static void AttachInspectStringComp(ThingDef thingDef, ResearchProjectDef rpd)
        {
            if (compPropsCtor == null) return; // Arcane Technology renamed/removed the type - nothing to reflect onto

            if (thingDef.comps == null) thingDef.comps = new List<CompProperties>();
            if (thingDef.comps.Any(c => c != null && c.GetType() == compPropsType)) return; // already has one somehow

            if (compPropsCtor.Invoke(new object[] { rpd }) is CompProperties props)
            {
                thingDef.comps.Add(props);
            }
        }

        // Follows descriptionHyperlinks - with a visited set in case of a
        // chain of variants pointing at each other - until it finds a
        // ThingDef that's already in thingDic.
        private static ResearchProjectDef ResolveViaHyperlink(ThingDef def, HashSet<ThingDef> visited)
        {
            if (def == null || !visited.Add(def)) return null;

            var linked = GetHyperlinkedThingDef(def);
            if (linked == null) return null;

            if (Base.thingDic.TryGetValue(linked, out var rpd)) return rpd;

            return ResolveViaHyperlink(linked, visited);
        }

        private static ThingDef GetHyperlinkedThingDef(ThingDef def)
        {
            if (def.descriptionHyperlinks == null) return null;
            foreach (var link in def.descriptionHyperlinks)
            {
                if (link.def is ThingDef td) return td;
            }
            return null;
        }
    }
}
