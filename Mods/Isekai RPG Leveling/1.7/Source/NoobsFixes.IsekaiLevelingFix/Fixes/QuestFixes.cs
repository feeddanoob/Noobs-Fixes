using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.Quests;
using IsekaiLeveling.WorldBoss;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// #06. Hunt rewards of rank C and below hand out Steel, an industrial material that Medieval
	/// Overhaul's tech curve doesn't use. With Medieval Overhaul active they give its Iron Ingot
	/// instead, with the stack size recomputed from the ingot's value. The high-rank industrial
	/// branch (rank A+ and industrial tech) is left as Isekai rolls it.
	/// </summary>
	internal sealed class MedievalMaterialsFix : IsekaiFix
	{
		private const string MedievalOverhaul = "dankpyon.medieval.overhaul";
		private static MedievalMaterialsFix self;
		private static ThingDef ironIngot;
		private static Func<TechLevel> maxTechLevel;

		public MedievalMaterialsFix() { self = this; }

		public override string Id => "06";
		public override string Title => "Hunt material rewards give Iron Ingots instead of Steel with Medieval Overhaul";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.GenerateMaterials };

		public override string NotNeededReason() => IsekaiInstall.ModActive(MedievalOverhaul) ? null : "Medieval Overhaul is not active";

		public override void Apply(PatchScope p)
		{
			ironIngot = DefDatabase<ThingDef>.GetNamedSilentFail("DankPyon_IronIngot");
			p.Require(ironIngot != null, "Medieval Overhaul's DankPyon_IronIngot is not defined");
			maxTechLevel = p.Delegate<Func<TechLevel>>(p.Method(typeof(IncidentWorker_IsekaiHunt), "GetMaxTechLevel"));
			p.Postfix(p.Target(Anchors.GenerateMaterials), nameof(Postfix));
		}

		private static void Postfix(QuestRank rank, float targetValue, ref Thing __result)
		{
			if (!self.Active || __result == null || __result.def != ThingDefOf.Steel) return;
			try
			{
				if ((int)rank >= (int)QuestRank.A && (int)maxTechLevel() >= (int)TechLevel.Industrial) return;
				Thing ingots = ThingMaker.MakeThing(ironIngot);
				ingots.stackCount = Mathf.Clamp(Mathf.RoundToInt(targetValue / ironIngot.BaseMarketValue), 20, 150);
				__result = ingots;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "IncidentWorker_IsekaiHunt.GenerateMaterials");
			}
		}
	}

	/// <summary>
	/// #07. Hunt and world-boss quests try once to remove their world site when they end, and
	/// skip it if colonists are still on the map. Nothing retried, so those sites stayed on the
	/// world map forever (save bloat, phantom icon). A skipped site is now queued and retried
	/// every 250 ticks until the map is empty, whether or not guild quests are enabled.
	/// </summary>
	internal sealed class SiteRemovalFix : IsekaiFix
	{
		private static SiteRemovalFix self;
		private static readonly List<Site> queue = new List<Site>();
		private static AccessTools.FieldRef<QuestPart_IsekaiWorldHunt, bool> huntSiteCreated;
		private static AccessTools.FieldRef<QuestPart_WorldBoss, Site> bossSite;
		private static AccessTools.FieldRef<QuestPart_WorldBoss, bool> bossSiteCreated;

		public SiteRemovalFix() { self = this; }

		public override string Id => "07";
		public override string Title => "Finished hunt / world-boss sites are removed once colonists leave, not left forever";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.WorldHuntCheckLeftSite, Anchors.WorldHuntCleanup, Anchors.WorldBossTryRemoveSite, Anchors.HuntTrackerTick };

		public override void Apply(PatchScope p)
		{
			huntSiteCreated = p.FieldRef<QuestPart_IsekaiWorldHunt, bool>("siteCreated");
			bossSite = p.FieldRef<QuestPart_WorldBoss, Site>("site");
			bossSiteCreated = p.FieldRef<QuestPart_WorldBoss, bool>("siteCreated");
			p.Postfix(p.Target(Anchors.WorldHuntCheckLeftSite), nameof(HuntCheckLeft_Postfix));
			p.Postfix(p.Target(Anchors.WorldHuntCleanup), nameof(HuntCleanup_Postfix));
			p.Postfix(p.Target(Anchors.WorldBossTryRemoveSite), nameof(BossTryRemove_Postfix));
			p.Postfix(p.Target(Anchors.HuntTrackerTick), nameof(Tick_Postfix));
		}

		public override void OnGameLoaded() => queue.Clear();

		public override void Revert() => queue.Clear();

		private static void RemoveWhenClear(Site site)
		{
			if (site == null || !site.Spawned) return;
			if (!site.HasMap || !IncidentWorker_IsekaiHunt.AnyColonistsOnMap(site.Map))
			{
				Find.WorldObjects.Remove(site);
				queue.Remove(site);
				return;
			}
			if (!queue.Contains(site)) queue.Add(site);
		}

		// Isekai's own attempt ran first; a site still spawned afterwards was skipped because colonists were on it.
		private static void HuntCheckLeft_Postfix(QuestPart_IsekaiWorldHunt __instance)
		{
			if (!self.Active) return;
			try { RemoveWhenClear(__instance.site); }
			catch (Exception ex) { self.Fault(ex, "QuestPart_IsekaiWorldHunt.CheckIfColonistsLeftSite"); }
		}

		private static void HuntCleanup_Postfix(QuestPart_IsekaiWorldHunt __instance)
		{
			if (!self.Active) return;
			try { if (huntSiteCreated(__instance)) RemoveWhenClear(__instance.site); }
			catch (Exception ex) { self.Fault(ex, "QuestPart_IsekaiWorldHunt.Cleanup"); }
		}

		private static void BossTryRemove_Postfix(QuestPart_WorldBoss __instance)
		{
			if (!self.Active) return;
			try { if (bossSiteCreated(__instance)) RemoveWhenClear(bossSite(__instance)); }
			catch (Exception ex) { self.Fault(ex, "QuestPart_WorldBoss.TryRemoveSite"); }
		}

		private static void Tick_Postfix()
		{
			if (queue.Count == 0 || !self.Active) return;
			if (Find.TickManager.TicksGame % 250 != 0) return;
			try
			{
				for (int i = queue.Count - 1; i >= 0; i--)
				{
					Site site = queue[i];
					if (site == null || !site.Spawned)
					{
						queue.RemoveAt(i);
						continue;
					}
					if (!site.HasMap || !IncidentWorker_IsekaiHunt.AnyColonistsOnMap(site.Map))
					{
						queue.RemoveAt(i);
						Find.WorldObjects.Remove(site);
					}
				}
			}
			catch (Exception ex)
			{
				self.Fault(ex, "site removal retry (IsekaiHuntTracker.GameComponentTick)");
			}
		}
	}

	/// <summary>
	/// #09. When a title duel is voided before the challenger ever spawns (the champion brought
	/// company, or the wrong pawn arrived), Isekai concluded the duel but never discarded the
	/// challenger, who stayed a KeepForever world pawn for the rest of the save. The challenger
	/// is now discarded in exactly those two cases. A spawned challenger (a real duel, including
	/// recruiting a downed challenger) is never touched.
	/// </summary>
	internal sealed class TitleDuelDiscardFix : IsekaiFix
	{
		private static TitleDuelDiscardFix self;
		private static Func<Map, List<Pawn>> countedPlayerPawns;
		private static AccessTools.FieldRef<TitleDuelTracker, Pawn> champion;
		private static AccessTools.FieldRef<TitleDuelTracker, Pawn> challenger;
		private static Action<TitleDuelTracker, Pawn> discardChallenger;

		public TitleDuelDiscardFix() { self = this; }

		public override string Id => "09";
		public override string Title => "Voided title duels discard the unspawned challenger instead of leaving a world pawn behind";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.TitleDuelHandleArrival };

		public override void Apply(PatchScope p)
		{
			countedPlayerPawns = p.Delegate<Func<Map, List<Pawn>>>(p.Method(typeof(TitleDuelTracker), "CountedPlayerPawns"));
			champion = p.FieldRef<TitleDuelTracker, Pawn>("champion");
			challenger = p.FieldRef<TitleDuelTracker, Pawn>("challenger");
			discardChallenger = p.Delegate<Action<TitleDuelTracker, Pawn>>(p.Method(typeof(TitleDuelTracker), "DiscardChallenger"));
			var target = p.Target(Anchors.TitleDuelHandleArrival);
			p.Prefix(target, nameof(Prefix));
			p.Postfix(target, nameof(Postfix));
		}

		// Decide up front, with the same test HandleArrival uses, whether this arrival voids the duel.
		private static void Prefix(TitleDuelTracker __instance, Map map, out Pawn __state)
		{
			__state = null;
			if (!self.Active) return;
			try
			{
				Pawn rival = challenger(__instance);
				if (rival == null || rival.Spawned) return;
				List<Pawn> present = countedPlayerPawns(map);
				if (present == null || present.Count == 0) return;
				if (present.Count > 1 || present[0] != champion(__instance)) __state = rival;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "TitleDuelTracker.HandleArrival (prefix)");
			}
		}

		private static void Postfix(TitleDuelTracker __instance, Pawn __state)
		{
			if (__state == null || !self.Active) return;
			try
			{
				if (!__state.Spawned) discardChallenger(__instance, __state);
			}
			catch (Exception ex)
			{
				self.Fault(ex, "TitleDuelTracker.HandleArrival (postfix)");
			}
		}
	}

	/// <summary>
	/// #10. GuildDropZoneUtility.TryDeliver wrapped both the drop-pod launch and the "delivered"
	/// letter in one try/catch. If the letter threw after the pods had left, it returned false
	/// and the caller placed the reward again on site: a duplicated reward. Now, once
	/// DropPodUtility.DropThingsNear has completed, the delivery counts as done.
	/// </summary>
	internal sealed class DropZoneDeliveryFix : IsekaiFix
	{
		private static DropZoneDeliveryFix self;
		[ThreadStatic] private static int depth;
		[ThreadStatic] private static bool podsLaunched;

		public DropZoneDeliveryFix() { self = this; }

		public override string Id => "10";
		public override string Title => "Guild drop-zone rewards are never placed a second time when the delivery letter fails";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.GuildDropZoneTryDeliver };

		public override void Apply(PatchScope p)
		{
			var target = p.Target(Anchors.GuildDropZoneTryDeliver);
			p.Prefix(target, nameof(Deliver_Prefix));
			p.Postfix(target, nameof(Deliver_Postfix));
			p.Finalizer(target, nameof(Deliver_Finalizer));

			List<MethodInfo> drops = typeof(DropPodUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(m => m.Name == nameof(DropPodUtility.DropThingsNear)).ToList();
			p.Require(drops.Count > 0, "DropPodUtility.DropThingsNear not found");
			foreach (MethodInfo m in drops) p.Postfix(m, nameof(Drop_Postfix));
		}

		private static void Deliver_Prefix(out bool __state)
		{
			__state = self.Active;
			if (!__state) return;
			depth++;
			podsLaunched = false;
		}

		// Only reached if DropThingsNear returned normally, i.e. the pods own the things now.
		private static void Drop_Postfix()
		{
			if (depth > 0) podsLaunched = true;
		}

		private static void Deliver_Postfix(ref bool __result, bool __state)
		{
			if (!__state || __result || !podsLaunched || !self.Active) return;
			__result = true;
			FixLog.Warn("#10: the guild drop-zone pods had already launched when Isekai's delivery letter failed (see Isekai's warning just above). "
				+ "Treating the delivery as done so the reward isn't placed a second time.");
		}

		private static Exception Deliver_Finalizer(Exception __exception, bool __state)
		{
			if (__state && depth > 0 && --depth == 0) podsLaunched = false;
			return __exception;
		}
	}
}
