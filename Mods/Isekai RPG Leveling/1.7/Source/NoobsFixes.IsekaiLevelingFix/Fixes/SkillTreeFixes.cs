using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.SkillTree;
using IsekaiLeveling.UI;
using UnityEngine;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// #02. PassiveTreeTracker caches the pawn's class gimmick and gimmick tiers. Unlocking a node
	/// through ForceUnlockNode (NPC auto-assign, Character Forge) cleared the bonus cache but not
	/// those two, so the gimmick could stay stale until something else invalidated it.
	///
	/// ForceUnlockNode is small enough that the 1.7 runtime may inline it into its only caller
	/// (TreeAutoAssigner.SpendPointsOnTree), where a patch on it would never run. So the caches
	/// are also cleared after SpendPointsOnTree, which is too large to be inlined.
	/// </summary>
	internal sealed class GimmickCacheFix : IsekaiFix
	{
		private static GimmickCacheFix self;
		private static FieldInfo cachedGimmick;
		private static FieldInfo gimmickTierCache;

		public GimmickCacheFix() { self = this; }

		public override string Id => "02";
		public override string Title => "Force-unlocking a constellation node refreshes the class gimmick";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.ForceUnlockNode, Anchors.SpendPointsOnTree };

		public override void Apply(PatchScope p)
		{
			cachedGimmick = p.Field(typeof(PassiveTreeTracker), "cachedGimmick");
			gimmickTierCache = p.Field(typeof(PassiveTreeTracker), "gimmickTierCache");
			p.Require(!cachedGimmick.FieldType.IsValueType || Nullable.GetUnderlyingType(cachedGimmick.FieldType) != null,
				"PassiveTreeTracker.cachedGimmick can no longer be cleared");
			p.Postfix(p.Target(Anchors.ForceUnlockNode), nameof(ForceUnlockNode_Postfix));
			p.Postfix(p.Target(Anchors.SpendPointsOnTree), nameof(SpendPointsOnTree_Postfix));
		}

		private static void ForceUnlockNode_Postfix(PassiveTreeTracker __instance) => Clear(__instance, "PassiveTreeTracker.ForceUnlockNode");

		private static void SpendPointsOnTree_Postfix(PassiveTreeTracker tracker) => Clear(tracker, "TreeAutoAssigner.SpendPointsOnTree");

		private static void Clear(PassiveTreeTracker tracker, string where)
		{
			if (!self.Active || tracker == null) return;
			try
			{
				cachedGimmick.SetValue(tracker, null);
				gimmickTierCache.SetValue(tracker, null);
			}
			catch (Exception ex)
			{
				self.Fault(ex, where);
			}
		}
	}

	/// <summary>
	/// #14. AutoSpendAccumulated spends an NPC's saved constellation points and then sets the
	/// remaining points to 0, even when the tree ran out of affordable nodes. Those points were
	/// lost. Now whatever SpendPointsOnTree couldn't place is kept for the next level-up.
	///
	/// AutoSpendAccumulated is small enough that the 1.7 runtime may inline it into its callers,
	/// where a patch on it would never run. So the bookkeeping sits on methods that can't be
	/// inlined: SpendPointsOnTree measures what was spent (every unlock costs exactly the node's
	/// cost, so spent = allocated after − before), and the leftover is put back right after
	/// AutoSpendAccumulated (if its patch runs) or after the IsekaiComponent method that called it
	/// (PostSpawnSetup / LevelUp / DevAddLevel). The initial class assignment
	/// (AssignTreeProgression) also spends and then zeroes points; it is left as Isekai does it.
	/// </summary>
	internal sealed class AutoSpendLeftoverFix : IsekaiFix
	{
		private static AutoSpendLeftoverFix self;
		[ThreadStatic] private static int assigning;
		private static readonly Dictionary<PassiveTreeTracker, KeyValuePair<int, int>> leftover = new Dictionary<PassiveTreeTracker, KeyValuePair<int, int>>();

		public AutoSpendLeftoverFix() { self = this; }

		public override string Id => "14";
		public override string Title => "NPC constellation auto-spend keeps points it couldn't place instead of deleting them";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[]
		{
			Anchors.AutoSpendAccumulated, Anchors.SpendPointsOnTree, Anchors.AssignTreeProgression,
			Anchors.IsekaiPostSpawnSetup, Anchors.IsekaiLevelUp, Anchors.IsekaiDevAddLevel,
		};

		public override void Apply(PatchScope p)
		{
			var assign = p.Target(Anchors.AssignTreeProgression);
			p.Prefix(assign, nameof(Assign_Prefix));
			p.Finalizer(assign, nameof(Assign_Finalizer));
			var spend = p.Target(Anchors.SpendPointsOnTree);
			p.Prefix(spend, nameof(Spend_Prefix));
			p.Postfix(spend, nameof(Spend_Postfix));
			p.Postfix(p.Target(Anchors.AutoSpendAccumulated), nameof(AutoSpend_Postfix));
			p.Postfix(p.Target(Anchors.IsekaiPostSpawnSetup), nameof(Caller_Postfix));
			p.Postfix(p.Target(Anchors.IsekaiLevelUp), nameof(Caller_Postfix));
			p.Postfix(p.Target(Anchors.IsekaiDevAddLevel), nameof(Caller_Postfix));
		}

		public override void OnGameLoaded() => leftover.Clear();

		public override void Revert() => leftover.Clear();

		private static void Assign_Prefix(out bool __state)
		{
			__state = self.Active;
			if (__state) assigning++;
		}

		private static Exception Assign_Finalizer(Exception __exception, bool __state)
		{
			if (__state && assigning > 0) assigning--;
			return __exception;
		}

		// AutoSpendAccumulated hands SpendPointsOnTree exactly the tracker's available points for its assigned tree.
		private static void Spend_Prefix(PassiveTreeTracker tracker, PassiveTreeDef tree, int budget, out int __state)
		{
			__state = int.MinValue;
			if (!self.Active || assigning > 0 || tracker == null || tree == null || budget <= 0) return;
			try
			{
				if (budget != tracker.availablePoints || tree.treeClass != tracker.assignedTree) return;
				__state = tracker.TotalAllocatedPoints;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "TreeAutoAssigner.SpendPointsOnTree (prefix)");
			}
		}

		private static void Spend_Postfix(PassiveTreeTracker tracker, int budget, int __state)
		{
			if (__state == int.MinValue || !self.Active) return;
			try
			{
				int left = budget - (tracker.TotalAllocatedPoints - __state);
				if (left > 0) leftover[tracker] = new KeyValuePair<int, int>(left, Find.TickManager?.TicksGame ?? 0);
				else leftover.Remove(tracker);
			}
			catch (Exception ex)
			{
				self.Fault(ex, "TreeAutoAssigner.SpendPointsOnTree (postfix)");
			}
		}

		private static void AutoSpend_Postfix(PassiveTreeTracker tracker) => Restore(tracker);

		private static void Caller_Postfix(IsekaiComponent __instance) => Restore(__instance?.passiveTree);

		private static void Restore(PassiveTreeTracker tracker)
		{
			if (tracker == null || leftover.Count == 0 || !self.Active) return;
			if (!leftover.TryGetValue(tracker, out KeyValuePair<int, int> entry)) return;
			leftover.Remove(tracker);
			// Only right after the spend (same tick), and only if Isekai zeroed the points.
			if (entry.Value == (Find.TickManager?.TicksGame ?? 0) && tracker.availablePoints == 0)
				tracker.availablePoints = entry.Key;
		}
	}

	/// <summary>
	/// #16. Cost and cooldown reductions (e.g. RimWorld of Magic's -% Mana Cost / -% Magic
	/// Cooldown nodes) store a positive value meaning "more reduction". IsInvertedStat flips the
	/// displayed sign correctly, but was also used to flip the colour, so a beneficial reduction
	/// was drawn red. Colour now follows the raw value's sign; the displayed sign is unchanged.
	/// Patched in the node tooltip and the tree summary's Active Bonuses list (the side panel
	/// was already right).
	/// </summary>
	internal sealed class BonusColorFix : IsekaiFix
	{
		private static BonusColorFix self;

		public BonusColorFix() { self = this; }

		public override string Id => "16";
		public override string Title => "Skill tree: cost/cooldown reductions are shown green, not red";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.SkillTreeNodeTooltip, Anchors.SkillTreeTreeSummary };

		public override void Apply(PatchScope p)
		{
			p.Method(typeof(Window_SkillTree), "IsInvertedStat");
			MethodInfo marker = AccessTools.Method(typeof(BonusColorFix), nameof(ColourUsesInversion));
			p.Transpiler(p.Target(Anchors.SkillTreeNodeTooltip), nameof(Transpiler), marker);
			p.Transpiler(p.Target(Anchors.SkillTreeTreeSummary), nameof(Transpiler), marker);
		}

		/// <summary>
		/// Feeds the colour decision through <see cref="ColourUsesInversion"/>. Only the first use
		/// of IsInvertedStat's result is the colour check; a later use (the displayed sign) stays.
		/// </summary>
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			MethodInfo isInverted = AccessTools.Method(typeof(Window_SkillTree), "IsInvertedStat");
			MethodInfo gate = AccessTools.Method(typeof(BonusColorFix), nameof(ColourUsesInversion));
			int patched = 0;
			for (int i = 0; i < list.Count; i++)
			{
				if (!Il.Calls(list[i], isInverted)) continue;
				if (i + 1 < list.Count && list[i + 1].opcode == OpCodes.Dup)
				{
					// Result duplicated: the top copy decides the colour, the copy underneath is
					// used later for the displayed sign. Gate only the top copy.
					list.Insert(i + 2, new CodeInstruction(OpCodes.Call, gate));
					patched++;
				}
				else if (i + 1 < list.Count && Il.IsStloc(list[i + 1]))
				{
					int slot = Il.LocalIndex(list[i + 1]);
					for (int j = i + 2; j < list.Count; j++)
					{
						if (Il.IsLdloc(list[j]) && Il.LocalIndex(list[j]) == slot)
						{
							list.Insert(j + 1, new CodeInstruction(OpCodes.Call, gate));
							patched++;
							break;
						}
					}
				}
				else
				{
					// The compiler kept the result on the stack for the ternary.
					list.Insert(i + 1, new CodeInstruction(OpCodes.Call, gate));
					patched++;
				}
			}
			TranspileReport.Result(self, original, patched == 1, "exactly one IsInvertedStat colour check");
			return list;
		}

		public static bool ColourUsesInversion(bool isInverted) => self.Active ? false : isInverted;
	}

	/// <summary>
	/// #17. The tree summary lists points spent/available but not which stats the class favours
	/// when its points are auto-distributed. Adds "Primary Stat" and "Secondary Stat" rows right
	/// after "Points Available", from Isekai's own class → stat table.
	/// </summary>
	internal sealed class ClassStatRowsFix : IsekaiFix
	{
		private delegate void SummaryLineDelegate(Window_SkillTree window, ref float cy, float w, string label, string value, Color? valueColor);

		private static ClassStatRowsFix self;
		private static AccessTools.FieldRef<Window_SkillTree, PassiveTreeDef> currentTree;
		private static Func<string, (IsekaiStatType, IsekaiStatType)?> classStatAffinity;
		private static SummaryLineDelegate drawSummaryLine;

		public ClassStatRowsFix() { self = this; }

		public override string Id => "17";
		public override string Title => "Skill tree summary shows the class's primary and secondary stat";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.SkillTreeTreeSummary, Anchors.SkillTreeSummaryLine, Anchors.GetClassStatAffinity };

		public override void Apply(PatchScope p)
		{
			currentTree = p.FieldRef<Window_SkillTree, PassiveTreeDef>("currentTree");
			classStatAffinity = p.Delegate<Func<string, (IsekaiStatType, IsekaiStatType)?>>(p.Method(typeof(IsekaiComponent), "GetClassStatAffinity"));
			drawSummaryLine = p.Delegate<SummaryLineDelegate>(p.Method(typeof(Window_SkillTree), "DrawSummaryLine"));
			p.Transpiler(p.Target(Anchors.SkillTreeTreeSummary), nameof(Transpiler),
				AccessTools.Method(typeof(ClassStatRowsFix), nameof(DrawClassStatRows)));
		}

		/// <summary>
		/// After the call that draws "Points Available", insert
		/// DrawClassStatRows(this, ref cy, w), reusing the exact argument loads of that call.
		/// </summary>
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			MethodInfo summaryLine = AccessTools.Method(typeof(Window_SkillTree), "DrawSummaryLine");
			bool done = false;
			int k = list.FindIndex(ci => ci.opcode == OpCodes.Ldstr && ci.operand as string == "Isekai_DetailPointsAvail");
			if (k >= 3 && list[k - 3].opcode == OpCodes.Ldarg_0 && IsAddressLoad(list[k - 2]) && IsValueLoad(list[k - 1]))
			{
				int call = list.FindIndex(k, ci => Il.Calls(ci, summaryLine));
				if (call > k)
				{
					list.InsertRange(call + 1, new[]
					{
						new CodeInstruction(OpCodes.Ldarg_0),
						new CodeInstruction(list[k - 2].opcode, list[k - 2].operand),
						new CodeInstruction(list[k - 1].opcode, list[k - 1].operand),
						new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ClassStatRowsFix), nameof(DrawClassStatRows))),
					});
					done = true;
				}
			}
			TranspileReport.Result(self, original, done, "the \"Points Available\" summary line");
			return list;
		}

		private static bool IsAddressLoad(CodeInstruction ci)
			=> ci.opcode == OpCodes.Ldarga || ci.opcode == OpCodes.Ldarga_S || ci.opcode == OpCodes.Ldloca || ci.opcode == OpCodes.Ldloca_S;

		private static bool IsValueLoad(CodeInstruction ci)
			=> ci.opcode == OpCodes.Ldarg || ci.opcode == OpCodes.Ldarg_S || ci.opcode == OpCodes.Ldarg_1 || ci.opcode == OpCodes.Ldarg_2
				|| ci.opcode == OpCodes.Ldarg_3 || Il.IsLdloc(ci);

		public static void DrawClassStatRows(Window_SkillTree window, ref float cy, float w)
		{
			if (!self.Active || window == null) return;
			try
			{
				var affinity = classStatAffinity(currentTree(window)?.treeClass);
				if (!affinity.HasValue) return;
				drawSummaryLine(window, ref cy, w, Tr("NoobsIsekai_DetailPrimaryStat", "Primary Stat"), StatName(affinity.Value.Item1), null);
				drawSummaryLine(window, ref cy, w, Tr("NoobsIsekai_DetailSecondaryStat", "Secondary Stat"), StatName(affinity.Value.Item2), null);
			}
			catch (Exception ex)
			{
				self.Fault(ex, "Window_SkillTree tree summary (class stat rows)");
			}
		}

		private static string StatName(IsekaiStatType stat) => Tr("Isekai_Stat_" + stat, stat.ToString());

		private static string Tr(string key, string fallback) => key.TryTranslate(out TaggedString s) ? s.ToString() : fallback;
	}

	/// <summary>
	/// #18 (performance). FindTreeForNode / FindNodeInAnyTree scanned every tree def per call,
	/// and TotalAllocatedPoints calls them once per unlocked node, every frame the skill tree
	/// window is open. Replaced by a node-id dictionary built once (defs don't change after load).
	/// </summary>
	internal sealed class NodeLookupFix : IsekaiFix
	{
		private static NodeLookupFix self;
		private static Dictionary<string, KeyValuePair<PassiveTreeDef, PassiveNodeRecord>> lookup;

		public NodeLookupFix() { self = this; }

		public override string Id => "18";
		public override string Title => "Constellation node lookups use a dictionary instead of scanning every tree";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.FindTreeForNode, Anchors.FindNodeInAnyTree };

		public override void Apply(PatchScope p)
		{
			p.Prefix(p.Target(Anchors.FindTreeForNode), nameof(FindTree_Prefix));
			p.Prefix(p.Target(Anchors.FindNodeInAnyTree), nameof(FindNode_Prefix));
		}

		private static Dictionary<string, KeyValuePair<PassiveTreeDef, PassiveNodeRecord>> Lookup()
		{
			if (lookup != null) return lookup;
			var d = new Dictionary<string, KeyValuePair<PassiveTreeDef, PassiveNodeRecord>>();
			foreach (PassiveTreeDef tree in DefDatabase<PassiveTreeDef>.AllDefs)
			{
				if (tree?.nodes == null) continue;
				foreach (PassiveNodeRecord node in tree.nodes)
				{
					// First tree wins, matching the original scan order.
					if (node?.nodeId != null && !d.ContainsKey(node.nodeId))
						d[node.nodeId] = new KeyValuePair<PassiveTreeDef, PassiveNodeRecord>(tree, node);
				}
			}
			return lookup = d;
		}

		private static bool FindTree_Prefix(string nodeId, ref PassiveTreeDef __result)
		{
			if (!self.Active || nodeId == null) return true;
			try
			{
				__result = Lookup().TryGetValue(nodeId, out var e) ? e.Key : null;
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "PassiveTreeTracker.FindTreeForNode");
				return true;
			}
		}

		private static bool FindNode_Prefix(string nodeId, ref PassiveNodeRecord __result)
		{
			if (!self.Active || nodeId == null) return true;
			try
			{
				__result = Lookup().TryGetValue(nodeId, out var e) ? e.Value : null;
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "PassiveTreeTracker.FindNodeInAnyTree");
				return true;
			}
		}
	}

	/// <summary>
	/// #25 (performance). The skill tree window looked up a class's tree with a LINQ scan of every
	/// tree def: on class change (LoadTree) and for every class tab on every frame
	/// (DrawClassPanel). LoadTree now uses a cached dictionary; DrawClassPanel's per-frame lookup
	/// runs the same search without LINQ allocations.
	/// </summary>
	internal sealed class TreeLookupFix : IsekaiFix
	{
		private static TreeLookupFix self;
		private static AccessTools.FieldRef<Window_SkillTree, PassiveTreeDef> currentTree;
		private static AccessTools.FieldRef<Window_SkillTree, string> selectedNodeId;
		private static Dictionary<string, PassiveTreeDef> treeByClass;

		public TreeLookupFix() { self = this; }

		public override string Id => "25";
		public override string Title => "Skill tree window finds class trees without a LINQ scan every frame";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.SkillTreeLoadTree, Anchors.SkillTreeClassPanel };

		public override void Apply(PatchScope p)
		{
			currentTree = p.FieldRef<Window_SkillTree, PassiveTreeDef>("currentTree");
			selectedNodeId = p.FieldRef<Window_SkillTree, string>("selectedNodeId");
			p.Prefix(p.Target(Anchors.SkillTreeLoadTree), nameof(LoadTree_Prefix));
			p.Transpiler(p.Target(Anchors.SkillTreeClassPanel), nameof(ClassPanel_Transpiler),
				AccessTools.Method(typeof(TreeLookupFix), nameof(FirstTree)));
		}

		private static bool LoadTree_Prefix(Window_SkillTree __instance, string treeClass)
		{
			if (!self.Active || string.IsNullOrEmpty(treeClass)) return true;
			try
			{
				if (treeByClass == null)
				{
					var d = new Dictionary<string, PassiveTreeDef>();
					foreach (PassiveTreeDef def in DefDatabase<PassiveTreeDef>.AllDefs)
					{
						if (!string.IsNullOrEmpty(def.treeClass) && !d.ContainsKey(def.treeClass)) d[def.treeClass] = def;
					}
					treeByClass = d;
				}
				currentTree(__instance) = treeByClass.TryGetValue(treeClass, out PassiveTreeDef tree) ? tree : null;
				selectedNodeId(__instance) = null;
				return false;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "Window_SkillTree.LoadTree");
				return true;
			}
		}

		private static IEnumerable<CodeInstruction> ClassPanel_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			MethodInfo fast = AccessTools.Method(typeof(TreeLookupFix), nameof(FirstTree));
			int replaced = 0;
			foreach (CodeInstruction ci in list)
			{
				if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo m
					&& m.Name == "FirstOrDefault" && m.DeclaringType == typeof(Enumerable) && m.IsGenericMethod
					&& m.GetGenericArguments()[0] == typeof(PassiveTreeDef) && m.GetParameters().Length == 2)
				{
					ci.opcode = OpCodes.Call;
					ci.operand = fast;
					replaced++;
				}
			}
			TranspileReport.Result(self, original, replaced > 0, "the class-tree FirstOrDefault lookup");
			return list;
		}

		/// <summary>Same result as Enumerable.FirstOrDefault, without the enumerator allocation for DefDatabase's list.</summary>
		public static PassiveTreeDef FirstTree(IEnumerable<PassiveTreeDef> source, Func<PassiveTreeDef, bool> predicate)
		{
			if (source is List<PassiveTreeDef> list)
			{
				for (int i = 0; i < list.Count; i++)
				{
					if (predicate(list[i])) return list[i];
				}
				return null;
			}
			return source.FirstOrDefault(predicate);
		}
	}
}
