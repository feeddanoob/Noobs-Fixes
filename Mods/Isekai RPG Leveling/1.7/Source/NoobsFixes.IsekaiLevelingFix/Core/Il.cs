using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>Small helpers for transpilers.</summary>
	internal static class Il
	{
		public static bool Calls(CodeInstruction ci, MethodBase method)
			=> (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase m && m == method;

		public static bool CallsNamed(CodeInstruction ci, string declaringTypeFullName, string name)
			=> (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
				&& ci.operand is MethodBase m && m.Name == name && m.DeclaringType?.FullName == declaringTypeFullName;

		public static bool IsStloc(CodeInstruction ci)
			=> ci.opcode == OpCodes.Stloc || ci.opcode == OpCodes.Stloc_S || ci.opcode == OpCodes.Stloc_0
				|| ci.opcode == OpCodes.Stloc_1 || ci.opcode == OpCodes.Stloc_2 || ci.opcode == OpCodes.Stloc_3;

		public static bool IsLdloc(CodeInstruction ci)
			=> ci.opcode == OpCodes.Ldloc || ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc_0
				|| ci.opcode == OpCodes.Ldloc_1 || ci.opcode == OpCodes.Ldloc_2 || ci.opcode == OpCodes.Ldloc_3;

		/// <summary>Local slot index of a ldloc/stloc, or -1.</summary>
		public static int LocalIndex(CodeInstruction ci)
		{
			if (ci.opcode == OpCodes.Ldloc_0 || ci.opcode == OpCodes.Stloc_0) return 0;
			if (ci.opcode == OpCodes.Ldloc_1 || ci.opcode == OpCodes.Stloc_1) return 1;
			if (ci.opcode == OpCodes.Ldloc_2 || ci.opcode == OpCodes.Stloc_2) return 2;
			if (ci.opcode == OpCodes.Ldloc_3 || ci.opcode == OpCodes.Stloc_3) return 3;
			switch (ci.operand)
			{
				case LocalBuilder lb: return lb.LocalIndex;
				case byte b: return b;
				case short s: return s;
				case ushort us: return us;
				case int i: return i;
				default: return -1;
			}
		}

		/// <summary>Moves labels and exception-block markers from <paramref name="from"/> onto <paramref name="to"/>.</summary>
		public static CodeInstruction TakeLabels(this CodeInstruction to, CodeInstruction from)
		{
			to.labels.AddRange(from.labels);
			from.labels.Clear();
			to.blocks.AddRange(from.blocks);
			from.blocks.Clear();
			return to;
		}
	}
}
