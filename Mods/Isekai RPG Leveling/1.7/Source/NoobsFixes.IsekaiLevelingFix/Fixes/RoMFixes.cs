using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IsekaiLeveling;
using IsekaiLeveling.Compatibility;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	internal static class RimWorldOfMagic
	{
		public const string PackageId = "Torann.ARimworldOfMagic";

		public static string NotActiveReason() => IsekaiInstall.ModActive(PackageId) ? null : "RimWorld of Magic is not active";
	}

	/// <summary>
	/// #15. Isekai computes STR → Might damage and WIS → Magic damage multipliers (plus the
	/// passive-tree RoM damage bonuses) but never applied them. Every RoM spell and Might ability
	/// reads the comp's arcaneDmg / mightPwr field directly at cast time, and RoM recomputes those
	/// fields in its own CompTick. This adds a CompTick postfix that applies the multipliers with
	/// Isekai's own non-compounding ApplyTrackedMultiplier (the same way Isekai already scales
	/// max mana / stamina and their regen). RimWorld of Magic's files are not modified.
	/// </summary>
	internal sealed class RoMDamageFix : IsekaiFix
	{
		private static RoMDamageFix self;
		private static Type magicType, mightType;
		private static FieldInfo arcaneDmg, mightPwr;
		private static Action<ThingComp, FieldInfo, Dictionary<int, (float, float)>, int, float> applyTrackedMultiplier;
		private static readonly Dictionary<int, (float, float)> magicTracker = new Dictionary<int, (float, float)>();
		private static readonly Dictionary<int, (float, float)> mightTracker = new Dictionary<int, (float, float)>();

		public RoMDamageFix() { self = this; }

		public override string Id => "15";
		public override string Title => "RimWorld of Magic: STR/WIS and passive-tree Might/Magic damage bonuses actually apply";
		public override FixKind Kind => FixKind.Bug;
		public override Anchor[] DependsOn => new[]
		{
			Anchors.RoMMagicTickPostfix, Anchors.RoMMightTickPostfix, Anchors.RoMApplyTrackedMultiplier,
			Anchors.RoMMagicDamageMultiplier, Anchors.RoMMightDamageMultiplier,
		};

		public override string NotNeededReason() => RimWorldOfMagic.NotActiveReason();

		public override void Apply(PatchScope p)
		{
			magicType = p.Type("TorannMagic.CompAbilityUserMagic");
			mightType = p.Type("TorannMagic.CompAbilityUserMight");
			arcaneDmg = p.Field(magicType, "arcaneDmg");
			mightPwr = p.Field(mightType, "mightPwr");
			p.Require(arcaneDmg.FieldType == typeof(float) && mightPwr.FieldType == typeof(float), "RoM's arcaneDmg/mightPwr are no longer floats");
			applyTrackedMultiplier = p.Delegate<Action<ThingComp, FieldInfo, Dictionary<int, (float, float)>, int, float>>(
				p.Method(typeof(RoMHarmonyPatches), "ApplyTrackedMultiplier"));

			// Same lookup Isekai uses for its own CompTick patches.
			MethodInfo magicTick = magicType.GetMethod("CompTick", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
			MethodInfo mightTick = mightType.GetMethod("CompTick", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
			p.Require(magicTick != null && mightTick != null, "RoM's CompTick methods not found");
			p.Postfix(magicTick, nameof(CompTick_Postfix));
			if (mightTick != magicTick) p.Postfix(mightTick, nameof(CompTick_Postfix));
		}

		public override void OnGameLoaded()
		{
			magicTracker.Clear();
			mightTracker.Clear();
		}

		public override void Revert() => OnGameLoaded();

		private static void CompTick_Postfix(ThingComp __instance)
		{
			if (!self.Active) return;
			try
			{
				if (!(__instance?.parent is Pawn pawn)) return;
				bool magic = magicType.IsInstanceOfType(__instance);
				if (!magic && !mightType.IsInstanceOfType(__instance)) return;
				IsekaiComponent comp = IsekaiComponent.GetCached(pawn);
				if (comp?.stats == null) return;

				if (magic)
					applyTrackedMultiplier(__instance, arcaneDmg, magicTracker, pawn.thingIDNumber, RoMCompatibility.GetMagicDamageMultiplier(comp));
				else
					applyTrackedMultiplier(__instance, mightPwr, mightTracker, pawn.thingIDNumber, RoMCompatibility.GetMightDamageMultiplier(comp));
			}
			catch (Exception ex)
			{
				self.Fault(ex, "RoM CompAbilityUserMagic/Might.CompTick");
			}
		}
	}

	/// <summary>
	/// #19 (performance). Isekai's RoM helpers looked up the same PropertyInfo/FieldInfo by name
	/// on every call: every ability cast, every summon, and every tick of the Chi hediff. The
	/// Type.GetProperty/GetField calls in those methods now go through a per-(type, name) cache.
	/// Results are identical; only the repeated reflection is gone.
	/// </summary>
	internal sealed class RoMReflectionCacheFix : IsekaiFix
	{
		private static RoMReflectionCacheFix self;
		private static readonly Dictionary<(Type, string, BindingFlags), PropertyInfo> properties = new Dictionary<(Type, string, BindingFlags), PropertyInfo>();
		private static readonly Dictionary<(Type, string, BindingFlags), FieldInfo> fields = new Dictionary<(Type, string, BindingFlags), FieldInfo>();

		public RoMReflectionCacheFix() { self = this; }

		public override string Id => "19";
		public override string Title => "RimWorld of Magic hooks cache their reflection lookups";
		public override FixKind Kind => FixKind.Performance;
		public override Anchor[] DependsOn => new[]
		{
			Anchors.RoMGetPawnFromAbility, Anchors.RoMSetCooldownTicksLeft, Anchors.RoMGetCooldownTicksLeft,
			Anchors.RoMChiPostfix, Anchors.RoMSummonPostfix,
		};

		public override string NotNeededReason() => RimWorldOfMagic.NotActiveReason();

		public override void Apply(PatchScope p)
		{
			MethodInfo[] markers =
			{
				AccessTools.Method(typeof(RoMReflectionCacheFix), nameof(CachedGetProperty)),
				AccessTools.Method(typeof(RoMReflectionCacheFix), nameof(CachedGetField)),
			};
			foreach (Anchor a in DependsOn) p.Transpiler(p.Target(a), nameof(Transpiler), markers);
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			var list = instructions.ToList();
			MethodInfo cachedProperty = AccessTools.Method(typeof(RoMReflectionCacheFix), nameof(CachedGetProperty));
			MethodInfo cachedField = AccessTools.Method(typeof(RoMReflectionCacheFix), nameof(CachedGetField));
			int replaced = 0;
			foreach (CodeInstruction ci in list)
			{
				if ((ci.opcode != OpCodes.Call && ci.opcode != OpCodes.Callvirt) || !(ci.operand is MethodInfo m) || m.DeclaringType != typeof(Type)) continue;
				ParameterInfo[] ps = m.GetParameters();
				if (ps.Length != 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(BindingFlags)) continue;
				if (m.Name == nameof(Type.GetProperty)) ci.operand = cachedProperty;
				else if (m.Name == nameof(Type.GetField)) ci.operand = cachedField;
				else continue;
				ci.opcode = OpCodes.Call; // instance call on Type → static call with the Type as first argument
				replaced++;
			}
			TranspileReport.Result(self, original, replaced > 0, "a Type.GetProperty/GetField lookup");
			return list;
		}

		public static PropertyInfo CachedGetProperty(Type type, string name, BindingFlags flags)
		{
			var key = (type, name, flags);
			if (!properties.TryGetValue(key, out PropertyInfo p))
			{
				p = type.GetProperty(name, flags);
				properties[key] = p;
			}
			return p;
		}

		public static FieldInfo CachedGetField(Type type, string name, BindingFlags flags)
		{
			var key = (type, name, flags);
			if (!fields.TryGetValue(key, out FieldInfo f))
			{
				f = type.GetField(name, flags);
				fields[key] = f;
			}
			return f;
		}
	}
}
