using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Confirms, at the end of startup and again on every game load, that each active fix is really
	/// in place:
	///   1. every Harmony patch it installed is still installed (nothing unpatched it);
	///   2. every code edit its transpilers make is present in the method's final compiled code,
	///      read back from Harmony after ALL mods' patches (so another mod's transpiler can't
	///      silently write over it);
	///   3. any non-Harmony change it made is still there (IsekaiFix.VerifyExtra).
	/// A fix that fails any check is switched off (all or nothing), so it can never run half-applied.
	/// </summary>
	internal static class PatchVerifier
	{
		public struct Result
		{
			public int PatchesChecked;
			public int CodeEditsChecked;
			public int FixesSwitchedOff;
			public List<string> Unverifiable;
		}

		public static Result VerifyAll()
		{
			var result = new Result { Unverifiable = new List<string>() };
			foreach (IsekaiFix fix in FixRegistry.Fixes)
			{
				if (!fix.Active) continue;
				string problem = Verify(fix, ref result);
				if (problem != null)
				{
					fix.Disrupt(problem);
					result.FixesSwitchedOff++;
				}
			}
			return result;
		}

		private static string Verify(IsekaiFix fix, ref Result result)
		{
			// 1. Installed?
			foreach (KeyValuePair<MethodBase, MethodInfo> p in fix.InstalledPatches)
			{
				Patches info = Harmony.GetPatchInfo(p.Key);
				if (!Contains(info, p.Value))
					return "its patch on " + FixLog.Describe(p.Key) + " is no longer installed (another mod removed it)";
				result.PatchesChecked++;
			}

			// 2. Our code edits survive in the final code?
			foreach (KeyValuePair<MethodBase, MethodInfo[]> m in fix.IlMarkers)
			{
				List<CodeInstruction> final;
				TranspileReport.Verifying = true;
				try
				{
					final = PatchProcessor.GetCurrentInstructions(m.Key, int.MaxValue, null);
				}
				catch (Exception ex)
				{
					// Not being able to read the code isn't proof it's broken: report, don't switch off.
					result.Unverifiable.Add(fix.Label + ": could not read the final code of " + FixLog.Describe(m.Key) + " (" + PatchScope.Root(ex).Message + ")");
					continue;
				}
				finally
				{
					TranspileReport.Verifying = false;
				}

				bool present = final != null && final.Any(ci => ci.operand is MethodInfo called && m.Value.Contains(called));
				if (!present)
					return "its code change to " + FixLog.Describe(m.Key) + " is missing from the final compiled code (another mod's transpiler probably rewrote that part)";
				result.CodeEditsChecked++;
			}

			// 3. Anything else the fix changed.
			return fix.VerifyExtra();
		}

		private static bool Contains(Patches info, MethodInfo patch)
		{
			if (info == null) return false;
			bool Ours(Patch p) => p.owner == FixRegistry.HarmonyId && p.PatchMethod == patch;
			return info.Prefixes.Any(Ours) || info.Postfixes.Any(Ours) || info.Transpilers.Any(Ours) || info.Finalizers.Any(Ours);
		}
	}
}
