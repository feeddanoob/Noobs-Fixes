using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.CharacterForge;
using IsekaiLeveling.Compatibility;
using IsekaiLeveling.Effects;
using IsekaiLeveling.Forge;
using IsekaiLeveling.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// #08. Mods like EdB Prepare Carefully can recreate the pawn/component during character
	/// creation, leaving IsekaiComponent.stats null; the Isekai panel then threw every frame and
	/// Isekai's bare catch hid it, so the panel just vanished. The panel now restores empty stats
	/// the same way Isekai does on spawn, and the swallowed error is logged once.
	/// </summary>
	internal sealed class CharacterCreationFix : IsekaiFix
	{
		private static CharacterCreationFix self;

		public CharacterCreationFix() { self = this; }

		public override string Id => "08";
		public override string Title => "Character-creation stats panel recovers from missing stats and logs its errors";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.CharacterCreationDrawPanel, Anchors.CharacterCreationPostfix };

		public override void Apply(PatchScope p)
		{
			p.Prefix(p.Target(Anchors.CharacterCreationDrawPanel), nameof(DrawPanel_Prefix));
			p.Transpiler(p.Target(Anchors.CharacterCreationPostfix), nameof(Postfix_Transpiler),
				AccessTools.Method(typeof(CharacterCreationFix), nameof(LogSwallowed)));
		}

		private static void DrawPanel_Prefix(IsekaiComponent comp)
		{
			if (!self.Active || comp == null || comp.stats != null) return;
			try { comp.stats = new IsekaiStatAllocation(); }
			catch (Exception ex) { self.Fault(ex, "Patch_CharacterCreation.DrawIsekaiPanel"); }
		}

		/// <summary>Turns the "pop" that discards the caught exception into a call that logs it (and then discards it).</summary>
		private static IEnumerable<CodeInstruction> Postfix_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			int replaced = 0;
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i].opcode == OpCodes.Pop && list[i].blocks.Any(b => b.blockType == ExceptionBlockType.BeginCatchBlock))
				{
					list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CharacterCreationFix), nameof(LogSwallowed))).TakeLabels(list[i]);
					replaced++;
				}
			}
			TranspileReport.Result(self, original, replaced == 1, "the silent catch block");
			return list;
		}

		public static void LogSwallowed(Exception ex)
		{
			if (!self.Active) return;
			FixLog.ErrorOnce("Isekai's character-creation stats panel threw an error. Isekai hides it on purpose so character creation keeps working; "
				+ "logged once by fix #08:\n" + ex, "charcreation-panel");
		}
	}

	/// <summary>
	/// #11. The Character Forge's XML export pasted names and def names into the XML as-is, so a
	/// pawn named e.g. "Tom & Jerry" or with a quote produced XML that the Forge's own import
	/// (or any parser) can't read back. Every text value is now escaped. Isekai's export runs
	/// unchanged on an escaped copy of the spec; the original spec is not modified.
	/// </summary>
	internal sealed class ExportXmlEscapeFix : IsekaiFix
	{
		private static ExportXmlEscapeFix self;
		private static MethodInfo memberwiseClone;
		private static readonly char[] Special = { '&', '<', '>', '"', '\'' };

		public ExportXmlEscapeFix() { self = this; }

		public override string Id => "11";
		public override string Title => "Character Forge XML export escapes names (& < > \" ') so the export can be imported again";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.ExportXml };

		public override void Apply(PatchScope p)
		{
			memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
			p.Require(memberwiseClone != null, "object.MemberwiseClone not found");
			p.Prefix(p.Target(Anchors.ExportXml), nameof(Prefix));
		}

		private static void Prefix(ref IsekaiCharacterSpec s, ref string defName)
		{
			if (!self.Active || s == null) return;
			try
			{
				var escaped = (IsekaiCharacterSpec)EscapedCopy(s, 0);
				defName = Escape(defName);
				s = escaped;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "IsekaiCharacterForge.ExportXml");
			}
		}

		/// <summary>Shallow copy with every string escaped, recursing into Isekai's own spec classes and lists of them.</summary>
		private static object EscapedCopy(object source, int depth)
		{
			object copy = memberwiseClone.Invoke(source, null);
			foreach (FieldInfo f in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (f.IsInitOnly || f.IsLiteral) continue;
				object value = f.GetValue(source);
				if (value == null) continue;
				if (value is string str)
				{
					f.SetValue(copy, Escape(str));
				}
				else if (depth < 2 && value is IList list && value.GetType().IsGenericType)
				{
					var newList = (IList)Activator.CreateInstance(value.GetType());
					foreach (object item in list)
					{
						newList.Add(item is string si ? Escape(si)
							: item != null && IsSpec(item.GetType()) ? EscapedCopy(item, depth + 1)
							: item);
					}
					f.SetValue(copy, newList);
				}
				else if (depth < 2 && IsSpec(value.GetType()))
				{
					f.SetValue(copy, EscapedCopy(value, depth + 1));
				}
			}
			return copy;
		}

		private static bool IsSpec(Type t) => t.IsClass && t.Assembly == IsekaiInstall.Assembly && !typeof(Def).IsAssignableFrom(t);

		/// <summary>Same five replacements as System.Security.SecurityElement.Escape.</summary>
		public static string Escape(string s)
		{
			if (s == null || s.IndexOfAny(Special) < 0) return s;
			var sb = new StringBuilder(s.Length + 16);
			foreach (char c in s)
			{
				switch (c)
				{
					case '&': sb.Append("&amp;"); break;
					case '<': sb.Append("&lt;"); break;
					case '>': sb.Append("&gt;"); break;
					case '"': sb.Append("&quot;"); break;
					case '\'': sb.Append("&apos;"); break;
					default: sb.Append(c); break;
				}
			}
			return sb.ToString();
		}
	}

	/// <summary>
	/// #13. Once more than 200 pawns had aura state, AuraSystem evicted entries whose pawn it
	/// couldn't find on the map currently being viewed, so pawns on other maps or in caravans
	/// lost their aura state (it reset whenever you switched maps). Eviction now only drops
	/// pawns that no longer exist anywhere. Isekai's "clear everything above 200" backstop stays.
	/// </summary>
	internal sealed class AuraEvictionFix : IsekaiFix
	{
		private const int Threshold = 200;
		private static AuraEvictionFix self;
		private static IDictionary states;

		public AuraEvictionFix() { self = this; }

		public override string Id => "13";
		public override string Title => "Aura cache only forgets pawns that are really gone, not pawns on other maps";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.GetAuraState };

		public override void Apply(PatchScope p)
		{
			states = p.Field(typeof(AuraSystem), "PawnAuraStates").GetValue(null) as IDictionary;
			p.Require(states != null, "AuraSystem.PawnAuraStates is no longer a dictionary");
			p.Prefix(p.Target(Anchors.GetAuraState), nameof(Prefix));
		}

		// Runs before GetAuraState; once this has evicted, Isekai's own (current-map-only) eviction no longer triggers.
		private static void Prefix()
		{
			if (!self.Active || states.Count <= Threshold) return;
			try
			{
				var alive = new HashSet<int>();
				foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
				{
					if (pawn != null && !pawn.Destroyed) alive.Add(pawn.thingIDNumber);
				}
				var gone = new List<object>();
				foreach (DictionaryEntry e in states)
				{
					if (!(e.Key is int id) || !alive.Contains(id)) gone.Add(e.Key);
				}
				foreach (object key in gone) states.Remove(key);
				if (states.Count > Threshold) states.Clear();
			}
			catch (Exception ex)
			{
				self.Fault(ex, "AuraSystem.GetAuraState");
			}
		}
	}

	/// <summary>
	/// #24 (performance). The Runic Station window rescanned every thing on the map, twice, on
	/// every GUI frame to list equipment and rune items. Results are now reused for half a
	/// second, and dropped at once if any listed item has been destroyed (e.g. a rune consumed).
	/// </summary>
	internal sealed class RunicStationCacheFix : IsekaiFix
	{
		private const float Interval = 0.5f;

		private sealed class Cache
		{
			public List<Thing> Equipment;
			public float EquipmentTime;
			public List<Thing> Runes;
			public bool RunesForWeapon;
			public float RunesTime;
		}

		private static RunicStationCacheFix self;
		private static readonly ConditionalWeakTable<Window_RunicStation, Cache> caches = new ConditionalWeakTable<Window_RunicStation, Cache>();

		public RunicStationCacheFix() { self = this; }

		public override string Id => "24";
		public override string Title => "Runic Station window reuses its map scan for half a second instead of rescanning every frame";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[] { Anchors.RunicGetAllEquipment, Anchors.RunicGetRuneItems };

		public override void Apply(PatchScope p)
		{
			var equipment = p.Target(Anchors.RunicGetAllEquipment);
			p.Prefix(equipment, nameof(Equipment_Prefix));
			p.Postfix(equipment, nameof(Equipment_Postfix));
			var runes = p.Target(Anchors.RunicGetRuneItems);
			p.Prefix(runes, nameof(Runes_Prefix));
			p.Postfix(runes, nameof(Runes_Postfix));
		}

		private static bool Intact(List<Thing> things)
		{
			for (int i = 0; i < things.Count; i++)
			{
				if (things[i] == null || things[i].Destroyed) return false;
			}
			return true;
		}

		private static bool Equipment_Prefix(Window_RunicStation __instance, ref List<Thing> __result, out bool __state)
		{
			__state = false;
			if (!self.Active) return true;
			try
			{
				Cache c = caches.GetOrCreateValue(__instance);
				if (c.Equipment != null && Time.realtimeSinceStartup - c.EquipmentTime < Interval && Intact(c.Equipment))
				{
					__result = c.Equipment;
					return false;
				}
				__state = true;
				return true;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "Window_RunicStation.GetAllEquipment");
				return true;
			}
		}

		private static void Equipment_Postfix(Window_RunicStation __instance, List<Thing> __result, bool __state)
		{
			if (!__state || __result == null || !self.Active) return;
			Cache c = caches.GetOrCreateValue(__instance);
			c.Equipment = __result;
			c.EquipmentTime = Time.realtimeSinceStartup;
		}

		private static bool Runes_Prefix(Window_RunicStation __instance, CompForgeEnhancement comp, ref List<Thing> __result, out bool __state)
		{
			__state = false;
			if (!self.Active || comp?.parent?.def == null) return true;
			try
			{
				Cache c = caches.GetOrCreateValue(__instance);
				if (c.Runes != null && c.RunesForWeapon == comp.parent.def.IsWeapon
					&& Time.realtimeSinceStartup - c.RunesTime < Interval && Intact(c.Runes))
				{
					__result = c.Runes;
					return false;
				}
				__state = true;
				return true;
			}
			catch (Exception ex)
			{
				self.Fault(ex, "Window_RunicStation.GetAvailableRuneItems");
				return true;
			}
		}

		private static void Runes_Postfix(Window_RunicStation __instance, CompForgeEnhancement comp, List<Thing> __result, bool __state)
		{
			if (!__state || __result == null || !self.Active) return;
			Cache c = caches.GetOrCreateValue(__instance);
			c.Runes = __result;
			c.RunesForWeapon = comp.parent.def.IsWeapon;
			c.RunesTime = Time.realtimeSinceStartup;
		}
	}

	/// <summary>
	/// #26. The "Auto-Distribute Points" button in Isekai's Character Editor window uses its own
	/// loop: STR→DEX→VIT→INT→WIS→CHA, restarting at STR on every click (one point always goes to
	/// STR), ignoring the pawn's class and the max stat cap. Isekai's 2026-10-14 update fixed exactly
	/// this in IsekaiComponent.AutoDistributeByClass (class-weighted, continues where the last call
	/// stopped, respects the cap) but didn't route this button through it. Now it does: the button
	/// runs the author's method.
	/// </summary>
	internal sealed class CharacterEditorDistributeFix : IsekaiFix
	{
		private const string CharacterEditor = "void.charactereditor";
		private static CharacterEditorDistributeFix self;

		public CharacterEditorDistributeFix() { self = this; }

		public override string Id => "26";
		public override string Title => "Character Editor's Auto-Distribute button uses Isekai's own fixed, class-aware distribution";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[] { Anchors.CharacterEditorAutoDistribute };

		public override string NotNeededReason() => IsekaiInstall.ModActive(CharacterEditor) ? null : "Character Editor is not active";

		public override void Apply(PatchScope p)
		{
			p.Method(typeof(IsekaiComponent), nameof(IsekaiComponent.AutoDistributeByClass));
			p.Prefix(p.Target(Anchors.CharacterEditorAutoDistribute), nameof(Prefix));
		}

		private static bool Prefix(IsekaiComponent comp)
		{
			if (!self.Active || comp?.stats == null) return true;
			try
			{
				comp.AutoDistributeByClass();
				return false;
			}
			catch (Exception ex)
			{
				// Points already placed stay placed; Isekai's editor loop places whatever is left.
				self.Fault(ex, "CharacterEditorCompatibility.AutoDistributePoints");
				return true;
			}
		}
	}
}
