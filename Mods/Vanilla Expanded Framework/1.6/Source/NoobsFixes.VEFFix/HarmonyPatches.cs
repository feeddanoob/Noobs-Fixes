using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace NoobsFixes.VEFFix
{
	[StaticConstructorOnStartup]
	public static class VEFFixMod
	{
		static VEFFixMod()
		{
			// LoadFolders.xml already gates this whole assembly behind
			// IfModActive="OskarPotocki.VanillaFactionsExpanded.Core", but check again here and
			// swallow any patch failure so a VEF update can't crash the rest of Noob's Edits.
			if (!ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core"))
			{
				return;
			}
			Harmony harmony = new Harmony("com.feeddanoob.NoobEdit.VEFFix");

			try
			{
				harmony.CreateClassProcessor(typeof(Pawn_GetGizmos_DedupeAbilityGizmos)).Patch();
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.VEFFix] Failed to patch Pawn.GetGizmos - animals with VEF-granted abilities will keep showing each ability button twice. " + ex);
			}

			// Kept separate so a future MVCF change only disables this patch, not the one above.
			try
			{
				if (Verb_OrderForceTarget_KeepMVCFCurrentVerb.TryInit())
				{
					harmony.CreateClassProcessor(typeof(Verb_OrderForceTarget_KeepMVCFCurrentVerb)).Patch();
				}
				else
				{
					Log.Warning("[NoobsFixes.VEFFix] Could not find MVCF's PawnVerbUtility.Manager / VerbManager.CurrentVerb - ability verbs ordered by hand may still become an animal's stuck MVCF attack verb.");
				}
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.VEFFix] Failed to patch Verb.OrderForceTarget - ability verbs ordered by hand may still become a pawn's stuck MVCF attack verb. " + ex);
			}

			try
			{
				harmony.CreateClassProcessor(typeof(JobDriver_CastAbility_CooldownOnAbortedShot)).Patch();
				harmony.CreateClassProcessor(typeof(JobGiver_AIFightEnemy_TryGiveJob_ReconsiderAbilities)).Patch();
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.VEFFix] Failed to patch JobDriver_CastAbility - AI animals may keep retrying aborted shooting abilities instead of fighting. " + ex);
			}
		}
	}

	// Vanilla 1.6 Pawn.GetGizmos() already shows ability buttons for colony animals, and VEF's
	// AnimalBehaviours Pawn_GetGizmos_Patch adds them again for every animal registered by
	// CompInitialAbility (it only skips that in god mode), so each ability shows up twice.
	// Ability caches its Command, so the duplicate is the very same object yielded a second
	// time - dropping repeats by reference can never hide a distinct button.
	[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
	public static class Pawn_GetGizmos_DedupeAbilityGizmos
	{
		[HarmonyPriority(Priority.Last)]
		public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
		{
			if (__instance?.abilities == null)
			{
				foreach (Gizmo gizmo in __result)
				{
					yield return gizmo;
				}
				yield break;
			}
			List<Command_Ability> seen = null;
			foreach (Gizmo gizmo in __result)
			{
				if (gizmo is Command_Ability command)
				{
					if (seen == null)
					{
						seen = new List<Command_Ability>();
					}
					else if (seen.Exists(c => ReferenceEquals(c, command)))
					{
						continue;
					}
					seen.Add(command);
				}
				yield return gizmo;
			}
		}
	}

	// The AI (JobGiver_AIFightEnemy.GetAbilityJob) always tries an off-cooldown offensive ability
	// before melee. For Verb_AbilityShoot abilities the cooldown only starts when a shot is actually
	// fired, and the vanilla CastAbilityOnThing job doesn't set abilityCasting, so nothing starts it
	// when the warmup is aborted (target moves out of line of sight/range or inside minRange). The
	// thunderbeast's electric bolt has a 5 s warmup, so against a moving enemy it kept standing still
	// re-trying the bolt instead of charging in - and only fought normally after the player fired the
	// bolt by hand (putting it on cooldown). This starts the normal cooldown when such an AI cast
	// ends without having fired, mirroring what abilityCasting jobs already do, so the animal falls
	// back to melee until the bolt is ready again.
	[HarmonyPatch(typeof(JobDriver_CastAbility), nameof(JobDriver_CastAbility.Notify_Starting))]
	public static class JobDriver_CastAbility_CooldownOnAbortedShot
	{
		public static void Postfix(JobDriver_CastAbility __instance)
		{
			Job job = __instance.job;
			if (job?.ability == null || job.def == null || job.def.abilityCasting || !(job.verbToUse is Verb_AbilityShoot))
			{
				return;
			}
			Ability ability = job.ability;
			__instance.AddFinishAction(delegate
			{
				// A fired shot has already started the cooldown in Verb_AbilityShoot.TryCastShot.
				if (ability.CanCooldown && !ability.OnCooldown && !ability.Casting)
				{
					ability.StartCooldown(ability.def.cooldownTicksRange.RandomInRange);
				}
			});
		}
	}

	// JobGiver_AIFightEnemy only reconsiders abilities when a fight starts or when its AttackMelee job
	// expires (360-480 ticks). A released thunderbeast charging an enemy that started out of bolt range
	// therefore never re-checks the bolt until it is already in melee, so it almost never shoots. For
	// any animal (colony, allied or hostile - e.g. raider/caravan thunderbeasts use the same fight AI)
	// with an AI-usable shooting ability, shorten that melee job's expiry so the
	// fight AI re-evaluates every ReconsiderTicks while chasing; if the target is 2-15 tiles away with
	// LOS and the bolt is ready it casts, otherwise the identical melee job just continues (the job
	// tracker doesn't restart an identical think-tree job).
	[HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
	public static class JobGiver_AIFightEnemy_TryGiveJob_ReconsiderAbilities
	{
		private const int ReconsiderTicks = 120;

		public static void Postfix(Pawn pawn, Job __result)
		{
			if (__result == null || __result.def != JobDefOf.AttackMelee || pawn == null || !pawn.RaceProps.Animal || pawn.abilities == null)
			{
				return;
			}
			bool hasShootingAbility = false;
			foreach (Ability ability in pawn.abilities.AllAbilitiesForReading)
			{
				if (ability.def.aiCanUse && ability.verb is Verb_AbilityShoot)
				{
					hasShootingAbility = true;
					break;
				}
			}
			if (!hasShootingAbility)
			{
				return;
			}
			if (__result.expiryInterval <= 0 || __result.expiryInterval > ReconsiderTicks)
			{
				__result.expiryInterval = ReconsiderTicks;
			}
			__result.checkOverrideOnExpire = true;
		}
	}

	// MVCF's PatchSet_MultiVerb prefix on Verb.OrderForceTarget stores whatever ranged verb the
	// player aims by hand as the pawn's VerbManager.CurrentVerb. Abilities whose verb derives
	// from Verb_Shoot (e.g. Alpha Animals' Verb_AbilityShoot bolts) reach that base method, but
	// MVCF never manages ability verbs, so the stored verb is never cleared during play. From
	// then on MVCF's TryGetAttackVerb returns it ahead of the pawn's melee verbs - a thunderbeast
	// defending its master stops charging in and stands back plinking bolts (ignoring the
	// ability cooldown) at 15-70% hit chance. This restores CurrentVerb to what it was before the
	// order whenever the ordered verb is an ability verb; the ordered job itself is unaffected.
	[HarmonyPatch(typeof(Verb), nameof(Verb.OrderForceTarget))]
	public static class Verb_OrderForceTarget_KeepMVCFCurrentVerb
	{
		private static MethodInfo managerMethod;

		private static FieldInfo currentVerbField;

		public static bool TryInit()
		{
			Type pawnVerbUtility = AccessTools.TypeByName("MVCF.Utilities.PawnVerbUtility");
			Type verbManager = AccessTools.TypeByName("MVCF.VerbManager");
			if (pawnVerbUtility == null || verbManager == null)
			{
				return false;
			}
			managerMethod = AccessTools.Method(pawnVerbUtility, "Manager", new[] { typeof(Pawn), typeof(bool) });
			currentVerbField = AccessTools.Field(verbManager, "CurrentVerb");
			return managerMethod != null && currentVerbField != null && currentVerbField.FieldType == typeof(Verb);
		}

		private static object GetManager(Pawn pawn)
		{
			return managerMethod.Invoke(null, new object[] { pawn, false });
		}

		[HarmonyPriority(Priority.First)]
		[HarmonyBefore("legodude17.mvcf")]
		public static void Prefix(Verb __instance, out Verb __state)
		{
			__state = null;
			if (!(__instance is IAbilityVerb) || __instance.CasterPawn == null)
			{
				return;
			}
			object manager = GetManager(__instance.CasterPawn);
			if (manager != null)
			{
				__state = (Verb)currentVerbField.GetValue(manager);
			}
		}

		public static void Postfix(Verb __instance, Verb __state)
		{
			if (!(__instance is IAbilityVerb) || __instance.CasterPawn == null)
			{
				return;
			}
			object manager = GetManager(__instance.CasterPawn);
			if (manager != null && currentVerbField.GetValue(manager) == __instance)
			{
				// If the previous value was already this ability verb (stuck before this fix was
				// installed), clear it rather than putting it back.
				currentVerbField.SetValue(manager, __state == __instance ? null : __state);
			}
		}
	}
}
