using System;
using HarmonyLib;
using RimWorld;
using TechBlock;
using Verse;

namespace NoobsFixes.TechBlockFix
{
	[StaticConstructorOnStartup]
	public static class TechBlockFixMod
	{
		static TechBlockFixMod()
		{
			// LoadFolders.xml already gates this whole assembly behind
			// IfModActive="fridgeBaron.TechBlock", but check again here and swallow
			// any patch failure so a missing/mismatched TechBlock install can't crash
			// the rest of Noob's Edits' Harmony patches.
			if (!ModsConfig.IsActive("fridgeBaron.TechBlock"))
			{
				return;
			}
			Harmony harmony = new Harmony("com.feeddanoob.NoobEdit.TechBlockFix");

			// Patched one class at a time (instead of a blanket PatchAll()) so that the
			// optional Arcane Technology compat patches below - which hard-reference an
			// assembly that may not be installed - can never take down this base patch
			// if that dependency is missing or fails to load.
			try
			{
				harmony.CreateClassProcessor(typeof(ResearchManager_FinishProject_RecalcTechBlock)).Patch();
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.TechBlockFix] Failed to patch TechBlock - understanding tech recalculation on research completion will not run. " + ex);
			}

			// Arcane Technology caches Faction.OfPlayer.def.techLevel into its own static
			// field (DArcaneTechnology.Base.playerTechLevel), refreshed only lazily (first
			// access) or when a research project finishes. TechBlock restores the correct
			// techLevel in its own GameComponent.LoadedGame()/StartedNewGame(), but nothing
			// tells Arcane Technology to re-read it afterwards - if anything reads its cache
			// first (e.g. an inspect pane, an outfit/storage filter), it locks onto whatever
			// techLevel happened to be in Faction.OfPlayer.def at that moment (the process's
			// original XML-authored default on a fresh launch) for the rest of the session,
			// showing everything as "Unknown technology" until the next research completes.
			// Only patched when Arcane Technology is actually active, and kept in its own
			// try/catch so a future Arcane Technology update can't break the patch above.
			if (ModsConfig.IsActive("dame.arcanetechnology"))
			{
				try
				{
					harmony.CreateClassProcessor(typeof(GameComponentUtility_LoadedGame_SyncArcaneTech)).Patch();
					harmony.CreateClassProcessor(typeof(GameComponentUtility_StartedNewGame_SyncArcaneTech)).Patch();
				}
				catch (Exception ex)
				{
					Log.Error("[NoobsFixes.TechBlockFix] Failed to patch Arcane Technology tech-level sync - items may show as \"Unknown technology\" until you finish another research project. " + ex);
				}
			}
		}
	}

	// TechBlock only recalculates the "Understanding" tech costs on game load, new game
	// start, or while its mod settings window is open - never when a normal research
	// project finishes during play. This postfix closes that gap.
	[HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
	public static class ResearchManager_FinishProject_RecalcTechBlock
	{
		public static void Postfix(ResearchProjectDef proj)
		{
			if (proj == null || TechBlocker.IsBlockTech(proj))
			{
				return;
			}
			TechBlock_Component component = Current.Game?.GetComponent<TechBlock_Component>();
			if (component == null || !component.inGame)
			{
				return;
			}
			component.techLevel = TechBlocker.RecalculateBlockValues(component.techLevel);
		}
	}

	// Runs after every GameComponent's LoadedGame() has fired (including TechBlock's own,
	// whichever order components happen to be in), i.e. right after a save finishes loading.
	[HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.LoadedGame))]
	public static class GameComponentUtility_LoadedGame_SyncArcaneTech
	{
		public static void Postfix()
		{
			DArcaneTechnology.Base.playerTechLevel = DArcaneTechnology.Base.GetPlayerTech();
		}
	}

	// Same as above, for the new-game path (GameComponentUtility.StartedNewGame runs after
	// TechBlock_Component.StartedNewGame() has set the initial techLevel).
	[HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.StartedNewGame))]
	public static class GameComponentUtility_StartedNewGame_SyncArcaneTech
	{
		public static void Postfix()
		{
			DArcaneTechnology.Base.playerTechLevel = DArcaneTechnology.Base.GetPlayerTech();
		}
	}
}
