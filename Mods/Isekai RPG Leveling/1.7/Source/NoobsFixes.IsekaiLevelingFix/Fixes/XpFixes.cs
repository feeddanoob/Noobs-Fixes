using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IsekaiLeveling;
using RimWorld;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// #03. IsekaiComponent batches small XP gains into one pending counter and flushes them
	/// together. The counter had no source, so batched Crafting/Mining/Skill XP was processed under
	/// whatever source triggered the flush, and CompTickRare's safety flush always used "Combat".
	/// The source decides the XP message and trait multipliers (Battle Maniac is 3x for Combat,
	/// 0.1x otherwise). This keeps one pending bucket per source and flushes each under its own
	/// label. Batching timing and thresholds are Isekai's own.
	///
	/// Runs after Isekai's own WIS prefix on GainXP, so the amounts it batches already include the
	/// WIS bonus. FlushPendingXP is small enough that the 1.7 runtime may inline it into
	/// CompTickRare, so the periodic flush is also done after CompTickRare (virtual, never inlined).
	/// </summary>
	internal sealed class XpBatchPerSourceFix : IsekaiFix
	{
		private static XpBatchPerSourceFix self;
		private static readonly Dictionary<IsekaiComponent, Dictionary<string, int>> pending = new Dictionary<IsekaiComponent, Dictionary<string, int>>();
		private static AccessTools.FieldRef<IsekaiComponent, int> lastFlushTick;
		private static AccessTools.FieldRef<IsekaiComponent, int> legacyPending;
		private static Action<IsekaiComponent, int, string> processXPGain;
		private static int batchInterval = 60;
		private static int immediateThreshold = 20;

		public XpBatchPerSourceFix() { self = this; }

		public override string Id => "03";
		public override string Title => "Batched XP keeps its real source (XP messages and trait multipliers like Battle Maniac)";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.GainXP, Anchors.FlushPendingXP, Anchors.IsekaiCompTickRare };

		public override void Apply(PatchScope p)
		{
			lastFlushTick = p.FieldRef<IsekaiComponent, int>("lastXPFlushTick");
			legacyPending = p.FieldRef<IsekaiComponent, int>("pendingXP");
			processXPGain = p.Delegate<Action<IsekaiComponent, int, string>>(p.Method(typeof(IsekaiComponent), "ProcessXPGain"));
			batchInterval = Constant(typeof(IsekaiComponent), "XP_BATCH_INTERVAL", 60);
			immediateThreshold = Constant(typeof(IsekaiComponent), "XP_IMMEDIATE_THRESHOLD", 20);

			p.Prefix(p.Target(Anchors.GainXP), nameof(GainXP_Prefix), Priority.Last);
			p.Prefix(p.Target(Anchors.FlushPendingXP), nameof(FlushPendingXP_Prefix));
			p.Postfix(p.Target(Anchors.IsekaiCompTickRare), nameof(CompTickRare_Postfix));
		}

		public override void OnGameLoaded() => pending.Clear();

		public override void Revert() => pending.Clear();

		/// <summary>Hand anything still batched back to Isekai's own counter so no XP is lost.</summary>
		protected override void OnFaulted()
		{
			foreach (var kv in pending)
			{
				int sum = 0;
				foreach (int v in kv.Value.Values) sum += v;
				if (sum > 0) legacyPending(kv.Key) += sum;
			}
			pending.Clear();
		}

		private static int Constant(Type type, string name, int fallback)
		{
			FieldInfo f = AccessTools.Field(type, name);
			return f != null && f.IsLiteral && f.GetRawConstantValue() is int v ? v : fallback;
		}

		private static bool GainXP_Prefix(IsekaiComponent __instance, int amount, string source)
		{
			if (!self.Active) return true;
			bool committed = false;
			try
			{
				// Same guards as Isekai's GainXP.
				Pawn pawn = __instance.Pawn;
				if (pawn?.Faction == null || !pawn.Faction.IsPlayer) return false;
				if (__instance.currentLevel >= (IsekaiMod.Settings?.MaxLevel ?? 1600)) return false;

				int now = Find.TickManager.TicksGame;
				string key = source ?? "";
				pending.TryGetValue(__instance, out Dictionary<string, int> buckets);
				AdoptLegacy(__instance, ref buckets);

				if (amount < immediateThreshold && now - lastFlushTick(__instance) < batchInterval)
				{
					if (buckets == null) pending[__instance] = buckets = new Dictionary<string, int>();
					buckets.TryGetValue(key, out int current);
					buckets[key] = current + amount;
					return false;
				}

				// Merge pending XP of the same source into this gain; other sources flush separately below.
				if (buckets != null && buckets.TryGetValue(key, out int same))
				{
					amount += same;
					buckets.Remove(key);
				}
				lastFlushTick(__instance) = now;

				committed = true;
				processXPGain(__instance, amount, source);
				FlushAll(__instance);
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "IsekaiComponent.GainXP");
				return !committed; // never let Isekai award XP a second time
			}
		}

		private static bool FlushPendingXP_Prefix(IsekaiComponent __instance)
		{
			if (!self.Active) return true;
			bool committed = false;
			try
			{
				lastFlushTick(__instance) = Find.TickManager.TicksGame;
				pending.TryGetValue(__instance, out Dictionary<string, int> buckets);
				AdoptLegacy(__instance, ref buckets);
				committed = true;
				FlushAll(__instance);
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "IsekaiComponent.FlushPendingXP");
				return !committed;
			}
		}

		// Backstop for an inlined FlushPendingXP: whatever is still batched gets flushed on the rare tick.
		private static void CompTickRare_Postfix(IsekaiComponent __instance)
		{
			if (!self.Active || pending.Count == 0 || !pending.ContainsKey(__instance)) return;
			Pawn pawn = __instance.Pawn;
			if (pawn == null || pawn.Dead) return; // same condition Isekai checks before its own flush
			try
			{
				lastFlushTick(__instance) = Find.TickManager.TicksGame;
				FlushAll(__instance);
			}
			catch (Exception ex)
			{
				self.Fault(ex, "IsekaiComponent.CompTickRare (XP flush)");
			}
		}

		/// <summary>XP that reached Isekai's single counter (e.g. before this fix was active) keeps Isekai's own "Combat" label.</summary>
		private static void AdoptLegacy(IsekaiComponent comp, ref Dictionary<string, int> buckets)
		{
			int legacy = legacyPending(comp);
			if (legacy <= 0) return;
			legacyPending(comp) = 0;
			if (buckets == null) pending[comp] = buckets = new Dictionary<string, int>();
			buckets.TryGetValue("Combat", out int current);
			buckets["Combat"] = current + legacy;
		}

		private static void FlushAll(IsekaiComponent comp)
		{
			if (!pending.TryGetValue(comp, out Dictionary<string, int> buckets)) return;
			pending.Remove(comp); // before processing: XP gains can re-enter GainXP
			foreach (var kv in buckets)
			{
				if (kv.Value > 0) processXPGain(comp, kv.Value, kv.Key.Length == 0 ? null : kv.Key);
			}
		}
	}

	/// <summary>
	/// #04. Patch_SkillLearnXP exists for direct-control mods (Perspective Shift) whose skill gains
	/// bypass jobs. For ordinary job-driven work, the job-completion XP patches already pay out, so
	/// this catch-all paid a second time. Skipped while the pawn has a job.
	/// </summary>
	internal sealed class SkillLearnXpFix : IsekaiFix
	{
		private static SkillLearnXpFix self;
		private static AccessTools.FieldRef<SkillRecord, Pawn> skillPawn;

		public SkillLearnXpFix() { self = this; }

		public override string Id => "04";
		public override string Title => "Skill-learning XP no longer double-counts XP that job completion already gives";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.SkillLearnXPPostfix };

		public override void Apply(PatchScope p)
		{
			skillPawn = p.FieldRef<SkillRecord, Pawn>("pawn");
			p.Prefix(p.Target(Anchors.SkillLearnXPPostfix), nameof(Prefix));
		}

		// Prefix on Isekai's own postfix; __0 is its SkillRecord argument.
		private static bool Prefix(SkillRecord __0)
		{
			if (!self.Active || __0 == null) return true;
			try
			{
				return skillPawn(__0)?.jobs?.curJob == null;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "Patch_SkillLearnXP.Postfix");
				return true;
			}
		}
	}

	/// <summary>
	/// #05. A hit fully absorbed by a shield belt or psychic shield deals no damage, but
	/// Patch_CombatXP_Melee still credited it: kill-XP attribution, class gimmicks (Divine
	/// Retribution, Inner Calm, Predator Focus, Counter Strike) and combat XP. Isekai's postfix
	/// can't see the "absorbed" result, so a first-priority postfix records it and Isekai's
	/// postfix is skipped for absorbed hits.
	/// </summary>
	internal sealed class AbsorbedHitXpFix : IsekaiFix
	{
		private static AbsorbedHitXpFix self;
		[ThreadStatic] private static Pawn lastVictim;
		[ThreadStatic] private static bool lastAbsorbed;

		public AbsorbedHitXpFix() { self = this; }

		public override string Id => "05";
		public override string Title => "Fully absorbed hits (shields) give no combat XP, kill credit or class-gimmick triggers";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.CombatXPMeleePostfix };

		public override void Apply(PatchScope p)
		{
			var preApplyDamage = AccessTools.Method(typeof(Pawn), nameof(Pawn.PreApplyDamage));
			p.Require(preApplyDamage != null, "Pawn.PreApplyDamage not found");
			p.Postfix(preApplyDamage, nameof(Capture_Postfix), Priority.First, new[] { IsekaiInstall.HarmonyId });
			p.Prefix(p.Target(Anchors.CombatXPMeleePostfix), nameof(Gate_Prefix));
		}

		private static void Capture_Postfix(Pawn __instance, bool absorbed)
		{
			lastVictim = __instance;
			lastAbsorbed = absorbed;
		}

		// Prefix on Isekai's own postfix; __0 is the damaged pawn.
		private static bool Gate_Prefix(Pawn __0)
		{
			if (!self.Active) return true;
			return !(lastAbsorbed && __0 == lastVictim);
		}
	}

	/// <summary>
	/// #20 (performance). DamageTracker.GetAttackers resolved each attacker id by scanning every
	/// world pawn and every map's pawns, once per attacker, on every kill. Same lookup, same
	/// places, same results, but one pass for all attackers.
	/// </summary>
	internal sealed class AttackerLookupFix : IsekaiFix
	{
		private static AttackerLookupFix self;
		private static FieldInfo damageRecords;

		public AttackerLookupFix() { self = this; }

		public override string Id => "20";
		public override string Title => "Kill-XP attacker lookup scans pawns once per kill, not once per attacker";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.DamageTrackerGetAttackers };

		public override void Apply(PatchScope p)
		{
			damageRecords = p.Field(typeof(IsekaiLeveling.Patches.DamageTracker), "damageRecords");
			p.Require(typeof(Dictionary<int, Dictionary<int, float>>).IsAssignableFrom(damageRecords.FieldType),
				"DamageTracker.damageRecords changed type");
			p.Prefix(p.Target(Anchors.DamageTrackerGetAttackers), nameof(Prefix));
		}

		private static bool Prefix(Thing target, ref Dictionary<Pawn, float> __result)
		{
			if (!self.Active) return true;
			try
			{
				var result = new Dictionary<Pawn, float>();
				if (target != null
					&& damageRecords.GetValue(null) is Dictionary<int, Dictionary<int, float>> all
					&& all.TryGetValue(target.thingIDNumber, out Dictionary<int, float> damage) && damage != null)
				{
					float total = 0f;
					foreach (var kv in damage) total += kv.Value;
					if (total > 0f)
					{
						var wanted = new HashSet<int>(damage.Keys);
						var found = new Dictionary<int, Pawn>();
						// Isekai's order: world pawns first, then each map.
						var world = Find.WorldPawns?.AllPawnsAlive;
						if (world != null)
						{
							foreach (Pawn pawn in world)
							{
								if (pawn != null && wanted.Contains(pawn.thingIDNumber) && !found.ContainsKey(pawn.thingIDNumber))
									found[pawn.thingIDNumber] = pawn;
							}
						}
						if (found.Count < wanted.Count)
						{
							foreach (Map map in Find.Maps)
							{
								foreach (Pawn pawn in map.mapPawns.AllPawns)
								{
									if (pawn != null && wanted.Contains(pawn.thingIDNumber) && !found.ContainsKey(pawn.thingIDNumber))
										found[pawn.thingIDNumber] = pawn;
								}
							}
						}
						foreach (var kv in damage)
						{
							if (found.TryGetValue(kv.Key, out Pawn attacker)) result[attacker] = kv.Value / total;
						}
					}
				}
				__result = result;
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "DamageTracker.GetAttackers");
				return true;
			}
		}
	}

	/// <summary>
	/// #21 (performance). Patch_EchoOfDefeat_Downed read Pawn_HealthTracker.pawn through
	/// Traverse, re-reflecting the field on every downing. Swapped for a cached field accessor.
	/// </summary>
	internal sealed class EchoOfDefeatFieldFix : IsekaiFix
	{
		private static EchoOfDefeatFieldFix self;
		private static AccessTools.FieldRef<Pawn_HealthTracker, Pawn> trackerPawn;

		public EchoOfDefeatFieldFix() { self = this; }

		public override string Id => "21";
		public override string Title => "Echo of Defeat reads the downed pawn through a cached accessor";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.EchoOfDefeatPostfix };

		public override void Apply(PatchScope p)
		{
			trackerPawn = p.FieldRef<Pawn_HealthTracker, Pawn>("pawn");
			p.Transpiler(p.Target(Anchors.EchoOfDefeatPostfix), nameof(Transpiler),
				AccessTools.Method(typeof(EchoOfDefeatFieldFix), nameof(TrackerPawn)));
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			bool done = false;
			for (int i = 0; i + 3 < list.Count; i++)
			{
				if (Il.CallsNamed(list[i], "HarmonyLib.Traverse", "Create")
					&& list[i + 1].opcode == OpCodes.Ldstr && list[i + 1].operand as string == "pawn"
					&& Il.CallsNamed(list[i + 2], "HarmonyLib.Traverse", "Field")
					&& Il.CallsNamed(list[i + 3], "HarmonyLib.Traverse", "GetValue"))
				{
					// Traverse.Create(tracker).Field("pawn").GetValue<Pawn>()  →  TrackerPawn(tracker)
					list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EchoOfDefeatFieldFix), nameof(TrackerPawn))).TakeLabels(list[i]);
					for (int k = 1; k <= 3; k++) list[i + k] = new CodeInstruction(OpCodes.Nop).TakeLabels(list[i + k]);
					done = true;
					break;
				}
			}
			TranspileReport.Result(self, original, done, "the Traverse lookup of Pawn_HealthTracker.pawn");
			return list;
		}

		public static Pawn TrackerPawn(object tracker)
		{
			if (tracker is Pawn_HealthTracker health) return trackerPawn(health);
			return Traverse.Create(tracker).Field("pawn").GetValue<Pawn>();
		}
	}
}
