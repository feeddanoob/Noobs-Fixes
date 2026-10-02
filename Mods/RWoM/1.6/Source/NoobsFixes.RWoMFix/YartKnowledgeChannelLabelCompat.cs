using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace NoobsFixes.RWoMFix
{
	// Optional, purely cosmetic compat with "Yet Another Research Tree" (YART, packageId
	// seohyeon.yart, C:\Steam\steamapps\workshop\content\294100\3745434121). YART gives
	// every KnowledgeCategoryDef its own per-channel research queue lane
	// (YART.Data.ResearchChannel.cs: ChannelRegistry.EnsureInit() iterates
	// DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading with no allowlist), which is
	// exactly what we want for TM_ArcaneKnowledge - but YART.Data.KnowledgeChannel's
	// constructor hardcodes the label as `anomalyPrefix + ": " + Category.LabelCap` for
	// any category that isn't vanilla's Basic/Advanced, where anomalyPrefix always comes
	// from ResearchTabDefOf.Anomaly regardless of which ResearchTabDef the category's
	// projects actually belong to. Without this, our channel shows as
	// "Anomaly: arcane knowledge" instead of "Magic: arcane knowledge".
	//
	// Patched via reflection with no compile-time reference to YART, so this degrades to a
	// harmless no-op (logged once) if YART is missing, updated its target's name, or
	// restructured KnowledgeChannel entirely - it can never break the actual gameplay
	// patches in HarmonyPatches.cs, which are applied separately.
	public static class YartKnowledgeChannelLabelCompat
	{
		private const string YartPackageId = "seohyeon.yart";
		private const string OurCategoryDefName = "TM_ArcaneKnowledge";
		private const string OurTabDefName = "TMagic";

		public static void TryPatch(Harmony harmony)
		{
			if (!ModsConfig.IsActive(YartPackageId))
			{
				return;
			}
			try
			{
				Type channelType = AccessTools.TypeByName("YART.Data.KnowledgeChannel");
				MethodInfo getLabel = channelType != null ? AccessTools.PropertyGetter(channelType, "Label") : null;
				if (getLabel == null)
				{
					Log.Warning("[NoobsFixes.RWoMFix] YART is active but YART.Data.KnowledgeChannel.Label could not be found - skipping the cosmetic channel-label fix (YART may have changed its internals).");
					return;
				}
				harmony.Patch(getLabel, postfix: new HarmonyMethod(typeof(YartKnowledgeChannelLabelCompat), nameof(Postfix)));
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.RWoMFix] Failed to patch YART's knowledge channel label (cosmetic only - safe to ignore). " + ex);
			}
		}

		public static void Postfix(object __instance, ref string __result)
		{
			KnowledgeCategoryDef category = Traverse.Create(__instance).Field("Category").GetValue<KnowledgeCategoryDef>();
			if (category == null || category.defName != OurCategoryDefName)
			{
				return;
			}
			ResearchTabDef tab = DefDatabase<ResearchTabDef>.GetNamedSilentFail(OurTabDefName);
			string prefix = tab != null ? (string)tab.LabelCap : "Magic";
			__result = prefix + ": " + category.LabelCap;
		}
	}
}
