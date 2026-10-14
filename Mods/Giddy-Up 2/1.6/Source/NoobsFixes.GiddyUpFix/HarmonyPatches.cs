using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace NoobsFixes.GiddyUpFix
{
	[StaticConstructorOnStartup]
	public static class GiddyUpFixMod
	{
		static GiddyUpFixMod()
		{
			// LoadFolders.xml already gates this whole assembly behind IfModActive="memegoddess.giddyup",
			// but check again here and swallow any patch failure so a Giddy-Up update can't crash the
			// rest of Noob's Edits.
			if (!ModsConfig.IsActive("memegoddess.giddyup"))
			{
				return;
			}
			Harmony harmony = new Harmony("com.feeddanoob.NoobEdit.GiddyUpFix");

			try
			{
				JobGiver_AIFollowPawn_RestoreForPlayerAnimals.Apply(harmony);
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.GiddyUpFix] Failed to patch JobGiver_AIFollowPawn - colony animals reserved as Giddy-Up mounts may still refuse to follow their master. " + ex);
			}

			try
			{
				if (MountUtility_Dismount_KeepPawnDutyFocus.TryApply(harmony))
				{
					harmony.CreateClassProcessor(typeof(JobGiver_AIDefendPawn_RepairEscortDuty)).Patch();
					harmony.CreateClassProcessor(typeof(JobGiver_AIFollowPawn_RepairEscortDuty)).Patch();
				}
				else
				{
					Log.Warning("[NoobsFixes.GiddyUpFix] Could not find GiddyUp.MountUtility.Dismount - NPC mounts may still lose their escort target when their rider dismounts.");
				}
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.GiddyUpFix] Failed to patch Giddy-Up dismount duty handling. " + ex);
			}
		}
	}

	// Giddy-Up 2 (caravan module, Patch_TradeCaravanAI) prefixes JobGiver_AIFollowPawn.TryGiveJob and
	// skips it for ANY animal whose Giddy-Up ReservedBy is set - meant for trader-caravan pack animals,
	// but it doesn't check faction, so a colony animal that a colonist has reserved as a mount never gets
	// a follow job: with its master drafted it just runs JobGiver_WanderNearMaster forever. Drafting /
	// undrafting only "fixed" it when the forced job end happened to clear the reservation. For
	// player-owned animals, when the result came back empty, run vanilla's unpatched follow logic and
	// use its answer. Non-player animals keep Giddy-Up's behaviour.
	public static class JobGiver_AIFollowPawn_RestoreForPlayerAnimals
	{
		public static MethodBase Original => AccessTools.Method(typeof(JobGiver_AIFollowPawn), "TryGiveJob");

		// Filled in by Harmony's reverse patcher with an unpatched copy of JobGiver_AIFollowPawn.TryGiveJob.
		public static Job VanillaTryGiveJob(JobGiver_AIFollowPawn instance, Pawn pawn)
		{
			throw new NotImplementedException("Reverse patch stub was not applied.");
		}

		public static void Postfix(JobGiver_AIFollowPawn __instance, Pawn pawn, ref Job __result)
		{
			if (__result != null || pawn == null || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Animal)
			{
				return;
			}
			__result = VanillaTryGiveJob(__instance, pawn);
		}

		public static void Apply(Harmony harmony)
		{
			MethodBase original = Original;
			harmony.CreateReversePatcher(original, new HarmonyMethod(AccessTools.Method(typeof(JobGiver_AIFollowPawn_RestoreForPlayerAnimals), nameof(VanillaTryGiveJob)))).Patch();
			harmony.Patch(original, postfix: new HarmonyMethod(AccessTools.Method(typeof(JobGiver_AIFollowPawn_RestoreForPlayerAnimals), nameof(Postfix))));
		}
	}

	// When a non-player rider dismounts, GiddyUp.MountUtility.Dismount does
	//   animal.mindState.duty.focus = animal.Position
	// for whatever duty the mount has. That parks location-based duties (DefendPoint, travel, ...), but
	// the Escort and Follow duties need a PAWN focus (JobGiver_AIDefendEscortee / AIFollowEscortee cast
	// it to Pawn), so a visiting caravan's mount ends up spamming "JobGiver_AIDefendEscortee has null
	// defendee" and stops escorting. This keeps Giddy-Up's dismount as-is but puts a pawn focus back if
	// Dismount replaced it, so location duties still get parked and pawn duties keep their escortee.
	public static class MountUtility_Dismount_KeepPawnDutyFocus
	{
		public sealed class DutyFocusState
		{
			public PawnDuty Duty;

			public LocalTargetInfo Focus;
		}

		public static bool TryApply(Harmony harmony)
		{
			Type mountUtility = AccessTools.TypeByName("GiddyUp.MountUtility");
			MethodInfo dismount = mountUtility == null ? null : AccessTools.Method(mountUtility, "Dismount");
			if (dismount == null || Array.Find(dismount.GetParameters(), p => p.Name == "animal" && p.ParameterType == typeof(Pawn)) == null)
			{
				return false;
			}
			harmony.Patch(dismount,
				prefix: new HarmonyMethod(AccessTools.Method(typeof(MountUtility_Dismount_KeepPawnDutyFocus), nameof(Prefix))),
				postfix: new HarmonyMethod(AccessTools.Method(typeof(MountUtility_Dismount_KeepPawnDutyFocus), nameof(Postfix))));
			return true;
		}

		public static void Prefix(Pawn animal, out DutyFocusState __state)
		{
			__state = null;
			PawnDuty duty = animal?.mindState?.duty;
			if (duty != null && duty.focus.Thing is Pawn)
			{
				__state = new DutyFocusState { Duty = duty, Focus = duty.focus };
			}
		}

		public static void Postfix(Pawn animal, DutyFocusState __state)
		{
			if (__state == null || animal?.mindState?.duty != __state.Duty)
			{
				return;
			}
			if (!(__state.Duty.focus.Thing is Pawn))
			{
				__state.Duty.focus = __state.Focus;
			}
		}
	}

	// Repairs pawns that were already broken by the above before this fix was installed (e.g. an
	// existing save): if an Escort/Follow-style job giver finds its duty focus is not a pawn, ask the
	// pawn's lord to re-issue everyone's duties - the lord knows who should be escorted, we don't.
	// Throttled per pawn; if the lord can't fix it, the job giver is skipped instead of erroring.
	internal static class EscortDutyRepair
	{
		private const int RepairInterval = 250;

		private static readonly Dictionary<int, int> lastRepairTick = new Dictionary<int, int>();

		public static bool FocusIsPawn(Pawn pawn)
		{
			return pawn.mindState?.duty?.focus.Thing is Pawn;
		}

		// Returns true if the job giver can run normally.
		public static bool EnsurePawnFocus(Pawn pawn)
		{
			if (pawn?.mindState?.duty == null || FocusIsPawn(pawn))
			{
				return true;
			}
			int now = Find.TickManager.TicksGame;
			if (!lastRepairTick.TryGetValue(pawn.thingIDNumber, out int last) || now - last >= RepairInterval)
			{
				lastRepairTick[pawn.thingIDNumber] = now;
				try
				{
					pawn.GetLord()?.CurLordToil?.UpdateAllDuties();
				}
				catch (Exception ex)
				{
					Log.WarningOnce("[NoobsFixes.GiddyUpFix] Lord duty refresh failed for " + pawn.ToStringSafe() + ": " + ex.Message, pawn.thingIDNumber ^ 0x6A11D);
				}
			}
			return FocusIsPawn(pawn);
		}
	}

	// TryGiveJob is declared on JobGiver_AIDefendPawn (JobGiver_AIDefendEscortee doesn't override it).
	[HarmonyPatch(typeof(JobGiver_AIDefendPawn), "TryGiveJob")]
	public static class JobGiver_AIDefendPawn_RepairEscortDuty
	{
		public static bool Prefix(JobGiver_AIDefendPawn __instance, Pawn pawn, ref Job __result)
		{
			if (!(__instance is JobGiver_AIDefendEscortee) || EscortDutyRepair.EnsurePawnFocus(pawn))
			{
				return true;
			}
			__result = null;
			return false;
		}
	}

	// TryGiveJob is declared on JobGiver_AIFollowPawn (JobGiver_AIFollowEscortee doesn't override it).
	[HarmonyPatch(typeof(JobGiver_AIFollowPawn), "TryGiveJob")]
	public static class JobGiver_AIFollowPawn_RepairEscortDuty
	{
		public static bool Prefix(JobGiver_AIFollowPawn __instance, Pawn pawn, ref Job __result)
		{
			if (!(__instance is JobGiver_AIFollowEscortee) || EscortDutyRepair.EnsurePawnFocus(pawn))
			{
				return true;
			}
			__result = null;
			return false;
		}
	}
}
