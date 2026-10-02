using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace NoobsFixes.RWoMFix
{
	[StaticConstructorOnStartup]
	public static class RWoMFixMod
	{
		static RWoMFixMod()
		{
			// LoadFolders.xml already gates this whole assembly behind
			// IfModActiveAll="Torann.ARimworldOfMagic,Ludeon.RimWorld.Anomaly", but check
			// again here and swallow any patch failure so a missing/mismatched install
			// can't crash the rest of Noob's Edits' Harmony patches.
			if (!ModsConfig.IsActive("Torann.ARimworldOfMagic") || !ModsConfig.IsActive("Ludeon.RimWorld.Anomaly"))
			{
				return;
			}
			Harmony harmony = new Harmony("com.feeddanoob.NoobEdit.RWoMStudyResearch");
			try
			{
				harmony.PatchAll();
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.RWoMFix] Failed to patch A Rimworld of Magic - Anomaly-style magic study research will not work correctly. " + ex);
			}

			// Optional, purely cosmetic - separate from PatchAll() above so that YART being
			// absent, or changing its internals, can never affect the patches that matter.
			YartKnowledgeChannelLabelCompat.TryPatch(harmony);
			SemiRandomResearchCompat.TryPatch(harmony);
		}
	}

	// The magic circles (TableMagicCircle, TableSmallMagicCircle) get CompStudiable added
	// via Patches\TM_StudyMagicCircles.xml, worked under the new TM_Magic-typed
	// TM_StudyMagicCircle WorkGiverDef (mage-only, via the MagicManipulation capacity).
	//
	// Studying anything with a KnowledgeCategory set (our circles included) is handled by
	// WorkGiver_DarkStudyInteract, not WorkGiver_StudyInteract (confirmed by decompiling
	// Assembly-CSharp.dll - StudyInteract explicitly rejects KnowledgeCategory != null,
	// DarkStudyInteract requires it - "Dark" is a legacy name, not an actual restriction to
	// evil content). Vanilla ships one DarkStudyInteract-typed WorkGiverDef (defName
	// "StudyInteract", under the DarkStudy work type) that scans for ANY KnowledgeCategory
	// thing map-wide with no capacity requirement - which would let any pawn with DarkStudy
	// work enabled study the circles too, not just mages. This blocks only that specific
	// vanilla WorkGiverDef instance from picking up the two circles, leaving our own
	// TM_Magic instance (same giverClass, different WorkGiverDef) unaffected.
	//
	// The reverse also applies: DarkStudyInteract scans every KnowledgeCategory thing on the
	// map, so without a restriction our TM_Magic instance would let mages study Anomaly
	// entities, the golden cube, etc. under Magic work. It's limited to the two circles only,
	// keeping Anomaly's own study mechanics exactly as vanilla has them.
	[HarmonyPatch(typeof(WorkGiver_DarkStudyInteract), nameof(WorkGiver_DarkStudyInteract.HasJobOnThing))]
	public static class WorkGiver_DarkStudyInteract_HasJobOnThing_RestrictMagicCircleToMages
	{
		public static bool Prefix(WorkGiver_DarkStudyInteract __instance, Thing t, ref bool __result)
		{
			bool isMagicCircle = t?.def != null &&
				(t.def.defName == "TableMagicCircle" || t.def.defName == "TableSmallMagicCircle");
			string giverDefName = __instance.def?.defName;
			if ((giverDefName == "StudyInteract" && isMagicCircle) ||
				(giverDefName == "TM_StudyMagicCircle" && !isMagicCircle))
			{
				__result = false;
				return false;
			}
			return true;
		}
	}

	// Vanilla hides every knowledge-category research project until the Void Monolith has
	// been activated at least once (EntityCodex.Hidden: knowledgeCategory != null &&
	// HighestLevelReached < 1 && GenerateMonolith). Hidden projects show as unknown, and
	// CanStartNow requires !IsHidden, so they can't be selected as the current project or
	// funded by grimoires/circles either. RWoM's tree has nothing to do with the monolith, so
	// TM_ArcaneKnowledge projects are always visible; Anomaly's own projects keep their gating.
	[HarmonyPatch(typeof(EntityCodex), nameof(EntityCodex.Hidden), new[] { typeof(ResearchProjectDef) })]
	public static class EntityCodex_Hidden_AlwaysShowArcaneResearch
	{
		public static void Postfix(ResearchProjectDef def, ref bool __result)
		{
			if (__result && def?.knowledgeCategory?.defName == "TM_ArcaneKnowledge")
			{
				__result = false;
			}
		}
	}

	// TM_ArcaneConstruction and TM_ArcaneFundamentals should always be researched from the
	// start, independent of whatever the chosen scenario grants.
	//
	// This has to run in TWO places, not just one:
	//  - Prefix on Game.InitNewGame: for a brand new colony, this is the *first* relevant
	//    hook point, running before map generation and starting-gear/loot generation. That
	//    ordering matters a lot here: any grimoire generated as starting/scattered loot
	//    locks in its research target once, permanently, in OnBookGenerated - and if
	//    TM_ArcaneFundamentals isn't finished yet at that moment, essentially the whole
	//    tree fails its "prerequisites completed" check, so the book's fallback logic grabs
	//    ANY valid project regardless of prerequisites, often something deep and
	//    permanently unstartable for that specific book (shows as a locked "when
	//    discovered" research benefit forever, and ReadingOutcomeDoerGainAnomalyResearch's
	//    DoesProvidesOutcome checks CanStartNow, so it correctly refuses to ever grant
	//    knowledge for a locked target - the book was just dealt a dead-end target at
	//    birth). Finishing the two free projects before any of that generation happens
	//    means new books always have real, startable prerequisites to lock onto.
	//  - Postfix on Game.FinalizeInit: safety net for a save that predates this mod, so an
	//    existing colony picks it up retroactively too (new books generated afterwards -
	//    e.g. from traders or quest rewards - benefit the same way as the InitNewGame case;
	//    books already generated before this patch keep whatever target they locked onto
	//    and can't be fixed retroactively, since that assignment is saved per-book).
	// Both are guarded by IsFinished so they're harmless no-ops after the first time.
	public static class AutoResearchArcaneBasics
	{
		private static readonly string[] FreeResearchDefNames = { "TM_ArcaneConstruction", "TM_ArcaneFundamentals" };

		public static void FinishFreeProjects()
		{
			foreach (string defName in FreeResearchDefNames)
			{
				ResearchProjectDef proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName);
				if (proj != null && !proj.IsFinished)
				{
					Find.ResearchManager.FinishProject(proj);
				}
			}
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
	public static class Game_InitNewGame_AutoResearchArcaneBasics
	{
		public static void Prefix()
		{
			AutoResearchArcaneBasics.FinishFreeProjects();
		}
	}

	[HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
	public static class Game_FinalizeInit_AutoResearchArcaneBasics
	{
		public static void Postfix()
		{
			AutoResearchArcaneBasics.FinishFreeProjects();
		}
	}
}
