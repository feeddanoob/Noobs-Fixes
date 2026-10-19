using System;
using System.Collections.Generic;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// #01. MobRankStatPatches scaled the MeleeDamageFactor stat by the rank/elite damage
	/// multiplier, and Thing_TakeDamage_Patch then scaled the final damage of every hit by the same
	/// multiplier again. Ranged hits got the multiplier once, melee hits got it squared. Removing
	/// MeleeDamageFactor from the stat set leaves the single, type-independent TakeDamage scaling.
	/// </summary>
	internal sealed class MeleeDamageOnceFix : IsekaiFix
	{
		private HashSet<StatDef> damageStats;
		private bool removed;

		public override string Id => "01";
		public override string Title => "Ranked/elite creatures' melee damage is multiplied once, not twice";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.MobRankStatPatchesCctor, Anchors.GetRankStatFactor, Anchors.TakeDamagePrefix };

		public override void Apply(PatchScope p)
		{
			damageStats = p.Field(typeof(MobRankStatPatches), "DamageStats").GetValue(null) as HashSet<StatDef>;
			p.Require(damageStats != null, "MobRankStatPatches.DamageStats is no longer a HashSet<StatDef>");
			p.Require(StatDefOf.MeleeDamageFactor != null, "the MeleeDamageFactor stat does not exist");
			p.Require(damageStats.Remove(StatDefOf.MeleeDamageFactor), "MeleeDamageFactor is not in Isekai's rank damage-stat list, nothing to remove");
			removed = true;
		}

		public override void Revert()
		{
			if (removed) damageStats?.Add(StatDefOf.MeleeDamageFactor);
			removed = false;
		}

		public override string VerifyExtra()
			=> damageStats != null && damageStats.Contains(StatDefOf.MeleeDamageFactor)
				? "MeleeDamageFactor is back in Isekai's rank damage-stat list (something re-added it), so melee is scaled twice again"
				: null;
	}

	/// <summary>
	/// #12. With Legacy Creature Ranking on, MobRankCalculator ignores World Rank Difficulty, but
	/// ReDeriveIfDifficultyChanged (run on load) still compared the difficulty stamp: changing the
	/// difficulty made every settled legacy creature look stale and reroll its random legacy rank.
	///
	/// ReDeriveIfDifficultyChanged is small and private, so the 1.7 runtime may inline it into its
	/// only caller, PostExposeData, where a patch on it would never run. Instead, just before
	/// PostExposeData's load step, a creature that is legacy-ranked under legacy mode gets its
	/// difficulty stamp brought up to date, so Isekai's own check sees nothing to re-derive.
	/// Switching Legacy mode off still re-derives, exactly as before.
	/// </summary>
	internal sealed class LegacyRankNoRerollFix : IsekaiFix
	{
		private static LegacyRankNoRerollFix self;
		private static AccessTools.FieldRef<MobRankComponent, bool> legacyStamp;
		private static AccessTools.FieldRef<MobRankComponent, int> difficultyStamp;

		public LegacyRankNoRerollFix() { self = this; }

		public override string Id => "12";
		public override string Title => "Legacy Creature Ranking: changing World Rank Difficulty no longer rerolls settled creatures";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.ReDeriveIfDifficultyChanged, Anchors.MobRankPostExposeData };

		public override void Apply(PatchScope p)
		{
			legacyStamp = p.FieldRef<MobRankComponent, bool>("rankLegacyStamp");
			difficultyStamp = p.FieldRef<MobRankComponent, int>("rankDifficultyStamp");
			p.Prefix(p.Target(Anchors.MobRankPostExposeData), nameof(Prefix));
		}

		private static void Prefix(MobRankComponent __instance)
		{
			if (!self.Active || Scribe.mode != LoadSaveMode.PostLoadInit) return;
			try
			{
				if (IsekaiLevelingSettings.UseLegacyCreatureRanking && legacyStamp(__instance))
					difficultyStamp(__instance) = MobRankUtility.DifficultyShift;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "MobRankComponent.PostExposeData");
			}
		}
	}

	/// <summary>
	/// #22 (performance). ProcessRaidPawns ranks each raider through AssignRaidPawnRank, which
	/// recomputes GetEffectiveRaidPoints and GetColonyBestRank (both scan every colonist) for
	/// every raider although neither depends on the raider. Inside one ProcessRaidPawns call,
	/// the first result of each is reused.
	/// </summary>
	internal sealed class RaidRankOncePerRaidFix : IsekaiFix
	{
		private static RaidRankOncePerRaidFix self;
		private static int depth;
		private static Map scopeMap;
		private static float scopeRawPoints;
		private static bool hasPoints, hasBest;
		private static float points;
		private static MobRankTier best;

		public RaidRankOncePerRaidFix() { self = this; }

		public override string Id => "22";
		public override string Title => "Raid ranking computes colony strength once per raid instead of once per raider";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.ProcessRaidPawns, Anchors.AssignRaidPawnRank, Anchors.GetEffectiveRaidPoints, Anchors.GetColonyBestRank };

		public override void Apply(PatchScope p)
		{
			var process = p.Target(Anchors.ProcessRaidPawns);
			p.Prefix(process, nameof(Process_Prefix));
			p.Finalizer(process, nameof(Process_Finalizer));
			var effective = p.Target(Anchors.GetEffectiveRaidPoints);
			p.Prefix(effective, nameof(Points_Prefix));
			p.Postfix(effective, nameof(Points_Postfix));
			var colonyBest = p.Target(Anchors.GetColonyBestRank);
			p.Prefix(colonyBest, nameof(Best_Prefix));
			p.Postfix(colonyBest, nameof(Best_Postfix));
		}

		public override void Revert() => Reset();

		private static void Reset()
		{
			depth = 0;
			scopeMap = null;
			hasPoints = hasBest = false;
		}

		private static void Process_Prefix(float raidPoints, Map map, out bool __state)
		{
			__state = self.Active;
			if (!__state) return;
			if (depth++ == 0)
			{
				scopeMap = map;
				scopeRawPoints = raidPoints;
				hasPoints = hasBest = false;
			}
		}

		private static Exception Process_Finalizer(Exception __exception, bool __state)
		{
			if (__state && depth > 0 && --depth == 0)
			{
				scopeMap = null;
				hasPoints = hasBest = false;
			}
			return __exception;
		}

		private static bool Points_Prefix(float rawPoints, Map map, ref float __result, out bool __state)
		{
			__state = false;
			if (depth == 0 || !self.Active || map != scopeMap || rawPoints != scopeRawPoints) return true;
			if (hasPoints)
			{
				__result = points;
				return false;
			}
			__state = true;
			return true;
		}

		private static void Points_Postfix(float __result, bool __state)
		{
			if (!__state) return;
			points = __result;
			hasPoints = true;
		}

		private static bool Best_Prefix(Map map, ref MobRankTier __result, out bool __state)
		{
			__state = false;
			if (depth == 0 || !self.Active || map != scopeMap) return true;
			if (hasBest)
			{
				__result = best;
				return false;
			}
			__state = true;
			return true;
		}

		private static void Best_Postfix(MobRankTier __result, bool __state)
		{
			if (!__state) return;
			best = __result;
			hasBest = true;
		}
	}
}
