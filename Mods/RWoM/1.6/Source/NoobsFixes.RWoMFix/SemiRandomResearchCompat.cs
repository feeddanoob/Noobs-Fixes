using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace NoobsFixes.RWoMFix
{
	// Optional integration with Semi Random Research (CM_Semi_Random_Research namespace, e.g.
	// "Semi Random Research:P-Fork Continued"), making arcane research work the same way it
	// handles Anomaly research: random offers, picked in its Next Research window (or
	// auto-picked), with YART's own controls locked as they are for every other category.
	//
	// Its ResearchTracker is already category-generic - it makes a queue per
	// KnowledgeCategoryDef, generates offers, auto-picks, and starts projects by category key,
	// so TM_ArcaneKnowledge works there without changes. Only its window isn't generic:
	// RebuildLeftColumnLists/DrawLeftColumn hardcode Standard, Gravship, Anomaly Basic and
	// Anomaly Advanced, so arcane offers and the active arcane project were never drawn. This
	// adds the active arcane project to its "currently researching" list and inserts an arcane
	// offers section after its Gravtech section, drawn with its own DrawResearchButton so
	// selecting and starting go through its normal footer Start button.
	//
	// Reflection-only, no compile-time reference; if anything it relies on is missing (e.g.
	// after an update) it logs a warning and leaves the window untouched.
	public static class SemiRandomResearchCompat
	{
		private const string ArcaneCategoryDefName = "TM_ArcaneKnowledge";
		private const string SectionLabel = "Available magic research";
		private const float RowHeight = 48f;
		private const float RowGap = 12f;
		private const float SectionGap = 8f;
		private const float HeaderHeight = 28f;
		private static readonly Color ArcaneColor = new Color(0.55f, 0.35f, 0.85f);

		private static Type windowType;
		private static FieldInfo availableField;
		private static FieldInfo activeListField;
		private static FieldInfo costWidthField;
		private static FieldInfo settingsField;
		private static FieldInfo allowSwitchingField;
		private static PropertyInfo trackerCurrentProjectProp;
		private static FieldInfo trackerAvailableField;
		private static MethodInfo drawResearchButton;
		private static readonly List<ResearchProjectDef> arcaneOffers = new List<ResearchProjectDef>();

		public static bool IsArcane(ResearchProjectDef proj)
		{
			return proj?.knowledgeCategory?.defName == ArcaneCategoryDefName;
		}

		public static void TryPatch(Harmony harmony)
		{
			windowType = AccessTools.TypeByName("CM_Semi_Random_Research.MainTabWindow_NextResearch");
			if (windowType == null)
			{
				return;
			}
			availableField = AccessTools.Field(windowType, "currentAvailableProjects");
			activeListField = AccessTools.Field(windowType, "cachedActiveProjects");
			costWidthField = AccessTools.Field(windowType, "cachedSharedCostWidth");
			drawResearchButton = AccessTools.Method(windowType, "DrawResearchButton");
			Type modType = AccessTools.TypeByName("CM_Semi_Random_Research.SemiRandomResearchMod");
			settingsField = modType != null ? AccessTools.Field(modType, "settings") : null;
			allowSwitchingField = settingsField != null ? AccessTools.Field(settingsField.FieldType, "allowSwitchingResearch") : null;
			Type trackerType = AccessTools.TypeByName("CM_Semi_Random_Research.ResearchTracker");
			trackerCurrentProjectProp = trackerType != null ? AccessTools.Property(trackerType, "CurrentProject") : null;
			trackerAvailableField = trackerType != null ? AccessTools.Field(trackerType, "currentAvailableProjects") : null;

			// Tracker-side fix, independent of the window patches below.
			MethodInfo getResearchable = trackerType != null ? AccessTools.Method(trackerType, "GetResearchableProjects") : null;
			if (getResearchable == null || trackerAvailableField == null)
			{
				Log.Warning("[NoobsFixes.RWoMFix] Semi Random Research is active but ResearchTracker.GetResearchableProjects was not found - its tech-level/cost filters may mix magic and regular research offers.");
			}
			else
			{
				try
				{
					harmony.Patch(getResearchable,
						prefix: new HarmonyMethod(typeof(SemiRandomResearchCompat), nameof(GetResearchableProjects_Prefix)),
						finalizer: new HarmonyMethod(typeof(SemiRandomResearchCompat), nameof(GetResearchableProjects_Finalizer)));
				}
				catch (Exception ex)
				{
					Log.Error("[NoobsFixes.RWoMFix] Failed to patch Semi Random Research ResearchTracker.GetResearchableProjects. " + ex);
				}
			}

			if (availableField == null || activeListField == null || costWidthField == null || drawResearchButton == null)
			{
				Log.Warning("[NoobsFixes.RWoMFix] Semi Random Research is active but its Next Research window layout wasn't recognized - magic research offers won't be shown there.");
				return;
			}

			Apply(harmony, AccessTools.Method(windowType, "RebuildLeftColumnLists"), "RebuildLeftColumnLists",
				postfix: new HarmonyMethod(typeof(SemiRandomResearchCompat), nameof(RebuildLeftColumnLists_Postfix)));
			Apply(harmony, AccessTools.Method(windowType, "DrawLeftColumn"), "DrawLeftColumn",
				transpiler: new HarmonyMethod(typeof(SemiRandomResearchCompat), nameof(DrawLeftColumn_Transpiler)));
			Apply(harmony, AccessTools.Method(windowType, "GetCategoryColor"), "GetCategoryColor",
				postfix: new HarmonyMethod(typeof(SemiRandomResearchCompat), nameof(GetCategoryColor_Postfix)));
		}

		private static void Apply(Harmony harmony, MethodInfo target, string label, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
		{
			if (target == null)
			{
				Log.Warning("[NoobsFixes.RWoMFix] Semi Random Research is active but MainTabWindow_NextResearch." + label + " was not found - magic research may not show correctly in its window.");
				return;
			}
			try
			{
				harmony.Patch(target, postfix: postfix, transpiler: transpiler);
			}
			catch (Exception ex)
			{
				Log.Error("[NoobsFixes.RWoMFix] Failed to patch Semi Random Research MainTabWindow_NextResearch." + label + ". " + ex);
			}
		}

		// Its "currently researching" list only takes Standard/Gravship/Anomaly projects.
		public static void RebuildLeftColumnLists_Postfix(object __instance, object researchTracker)
		{
			if (!(activeListField.GetValue(__instance) is List<ResearchProjectDef> active))
			{
				return;
			}
			ResearchProjectDef arcane = CurrentArcaneProject(researchTracker);
			if (arcane != null && !active.Contains(arcane))
			{
				active.Add(arcane);
			}
		}

		private static ResearchProjectDef CurrentArcaneProject(object tracker)
		{
			if (tracker != null && trackerCurrentProjectProp?.GetValue(tracker) is List<ResearchProjectDef> tracked)
			{
				foreach (ResearchProjectDef p in tracked)
				{
					if (IsArcane(p) && !p.IsFinished)
					{
						return p;
					}
				}
			}
			KnowledgeCategoryDef category = DefDatabase<KnowledgeCategoryDef>.GetNamedSilentFail(ArcaneCategoryDefName);
			ResearchProjectDef current = category != null ? Find.ResearchManager.GetProject(category) : null;
			return current != null && !current.IsFinished ? current : null;
		}

		// GetResearchableProjects(count, typeKey) builds one category's offers, but derives its
		// tech-level window (forceLowestTechLevel / one-higher-tech) and its cost-equalization
		// averages from the offers of EVERY category. RWoM's projects carry real tech levels
		// (Anomaly's have none), so a Neolithic magic offer pinned "lowest tech level" to
		// Neolithic and left zero regular offers once Neolithic bench research was done. For the
		// duration of the call, only offers on the same side (magic vs. non-magic) are visible;
		// non-magic categories keep seeing exactly what they did before, minus magic offers.
		public static void GetResearchableProjects_Prefix(object __instance, string typeKey, out List<ResearchProjectDef> __state)
		{
			__state = null;
			if (!(trackerAvailableField.GetValue(__instance) is List<ResearchProjectDef> offers))
			{
				return;
			}
			bool arcaneKey = typeKey == ArcaneCategoryDefName;
			if (!offers.Any(p => p != null && IsArcane(p) != arcaneKey))
			{
				return;
			}
			__state = offers;
			trackerAvailableField.SetValue(__instance, offers.Where(p => p != null && IsArcane(p) == arcaneKey).ToList());
		}

		public static void GetResearchableProjects_Finalizer(object __instance, List<ResearchProjectDef> __state)
		{
			if (__state != null)
			{
				trackerAvailableField.SetValue(__instance, __state);
			}
		}

		public static void GetCategoryColor_Postfix(ResearchProjectDef projectDef, ref Color __result)
		{
			if (IsArcane(projectDef))
			{
				__result = ArcaneColor;
			}
		}

		// Inserts DrawArcaneSection(this, ref y, viewRect) right after the Gravtech section, just
		// before DrawLeftColumn records the scroll height (if (Event.current.type == Repaint)
		// leftScrollViewHeight = y;), so the section is both drawn and counted in the scroll height.
		public static IEnumerable<CodeInstruction> DrawLeftColumn_Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> list = instructions.ToList();
			FieldInfo heightField = AccessTools.Field(windowType, "leftScrollViewHeight");
			MethodInfo getCurrentEvent = AccessTools.PropertyGetter(typeof(Event), nameof(Event.current));
			MethodInfo getRectWidth = AccessTools.PropertyGetter(typeof(Rect), nameof(Rect.width));

			int store = heightField != null ? list.FindLastIndex(ci => ci.StoresField(heightField)) : -1;
			int yLocal = store >= 1 && list[store - 1].IsLdloc() ? list[store - 1].LocalIndex() : -1;
			int insertAt = -1;
			for (int i = store - 1; i >= 0 && i >= store - 10; i--)
			{
				if (list[i].Calls(getCurrentEvent))
				{
					insertAt = i;
					break;
				}
			}
			int viewLocal = -1;
			for (int i = insertAt - 1; i > 0; i--)
			{
				if (list[i].Calls(getRectWidth) && (list[i - 1].opcode == OpCodes.Ldloca_S || list[i - 1].opcode == OpCodes.Ldloca))
				{
					viewLocal = list[i - 1].LocalIndex();
					break;
				}
			}
			if (yLocal < 0 || insertAt < 0 || viewLocal < 0)
			{
				Log.Warning("[NoobsFixes.RWoMFix] Semi Random Research's DrawLeftColumn didn't match the expected layout - magic research offers won't be shown in its window.");
				return list;
			}

			List<CodeInstruction> injected = new List<CodeInstruction>
			{
				new CodeInstruction(OpCodes.Ldarg_0),
				CodeInstruction.LoadLocal(yLocal, useAddress: true),
				CodeInstruction.LoadLocal(viewLocal),
				CodeInstruction.Call(typeof(SemiRandomResearchCompat), nameof(DrawArcaneSection)),
			};
			injected[0].MoveLabelsFrom(list[insertAt]);
			list.InsertRange(insertAt, injected);
			return list;
		}

		public static void DrawArcaneSection(object window, ref float y, Rect view)
		{
			try
			{
				if (!(availableField.GetValue(window) is List<ResearchProjectDef> available))
				{
					return;
				}
				ResearchProjectDef active = null;
				if (activeListField.GetValue(window) is List<ResearchProjectDef> activeList)
				{
					active = activeList.FirstOrDefault(IsArcane);
				}
				// Same rule as its Anomaly sections: while a project in the category is running,
				// its offers are hidden unless "allow switching research" is on.
				if (active != null && !active.IsFinished && !AllowSwitching())
				{
					return;
				}

				arcaneOffers.Clear();
				foreach (ResearchProjectDef p in available)
				{
					if (IsArcane(p) && p != active && !p.IsFinished)
					{
						arcaneOffers.Add(p);
					}
				}
				if (arcaneOffers.Count == 0)
				{
					return;
				}
				arcaneOffers.Sort((a, b) => a.CostApparent.CompareTo(b.CostApparent));
				float costWidth = (float)costWidthField.GetValue(window);

				y += SectionGap;
				GUI.color = new Color(0.4f, 0.4f, 0.4f, 0.4f);
				Widgets.DrawLineHorizontal(0f, y, view.width);
				GUI.color = Color.white;
				y += SectionGap;

				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleLeft;
				GUI.color = ArcaneColor;
				Widgets.Label(new Rect(0f, y, view.width, HeaderHeight), SectionLabel);
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				y += HeaderHeight;

				object[] args = new object[3];
				foreach (ResearchProjectDef p in arcaneOffers)
				{
					args[0] = new Rect(0f, y, view.width, RowHeight);
					args[1] = p;
					args[2] = costWidth;
					drawResearchButton.Invoke(window, args);
					y += RowHeight + RowGap;
				}
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[NoobsFixes.RWoMFix] Error drawing magic research in Semi Random Research's window. " + ex, 0x52574F4D);
			}
			finally
			{
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
			}
		}

		private static bool AllowSwitching()
		{
			object settings = settingsField?.GetValue(null);
			return settings != null && allowSwitchingField?.GetValue(settings) is bool allow && allow;
		}
	}
}
