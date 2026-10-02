using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace NoobsFixes.IsekaiCreaturesFix
{
	[StaticConstructorOnStartup]
	public static class IsekaiCreaturesFixMod
	{
		static IsekaiCreaturesFixMod()
		{
			// LoadFolders.xml already gates this whole assembly behind
			// IfModActive="Poupun.IsekaiCreaturesAddon", check again here and swallow
			// any patch failure so a missing/mismatched install can't crash the rest
			// of Noob's Edits' Harmony patches.
			if (!ModsConfig.IsActive("Poupun.IsekaiCreaturesAddon"))
			{
				return;
			}
			try
			{
				new Harmony("com.feeddanoob.NoobEdit.IsekaiCreaturesFix").PatchAll();
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.IsekaiCreaturesFix] Failed to patch Isekai Creatures - aggro line-of-sight fix will not run. " + ex);
			}
		}
	}

	// Isekai Creatures' CompCreatureAggro.FindNearbyTarget() picks manhunter/flee targets
	// by straight-line distance only - no line-of-sight check - so a creature (e.g. a
	// slime) goes manhunter on a colonist standing on the other side of a wall or around
	// a corner, with nothing actually visible or "in its path." This discards the picked
	// target if it isn't actually visible from the creature's position, using the same
	// GenSight.LineOfSightToThing check the addon's own melee-reach patch already uses.
	[HarmonyPatch]
	public static class CompCreatureAggro_LineOfSight_Patch
	{
		static MethodBase TargetMethod()
		{
			var type = AccessTools.TypeByName("IsekaiCreatures.CompCreatureAggro");
			return type == null ? null : AccessTools.Method(type, "FindNearbyTarget");
		}

		// No-ops cleanly if Isekai Creatures isn't installed, or a future update
		// renames/removes the method.
		static bool Prepare() => TargetMethod() != null;

		[HarmonyPostfix]
		public static void Postfix(Pawn pawn, ref Pawn __result)
		{
			if (__result == null || pawn?.Map == null) return;
			if (!GenSight.LineOfSightToThing(pawn.Position, __result, pawn.Map, skipFirstCell: true))
				__result = null;
		}
	}
}
