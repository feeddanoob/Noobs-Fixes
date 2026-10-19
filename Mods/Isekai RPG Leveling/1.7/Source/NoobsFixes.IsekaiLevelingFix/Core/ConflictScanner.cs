using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Reports other mods' Harmony patches on the methods the active fixes patch or rely on. Runs
	/// once after startup and once more on the first game load (some mods patch lazily). Each
	/// finding is reported once.
	/// </summary>
	internal static class ConflictScanner
	{
		private static readonly HashSet<string> reported = new HashSet<string>();

		public static void Scan(bool atStartup)
		{
			try
			{
				var lines = new List<string>();
				Assembly ours = typeof(ConflictScanner).Assembly;
				Assembly isekai = IsekaiInstall.Assembly;
				Assembly vanilla = typeof(Pawn).Assembly;

				foreach (IsekaiFix fix in FixRegistry.Fixes)
				{
					if (!fix.Active) continue;

					foreach (MethodBase method in fix.AnchorMethods.Concat(fix.PatchedMethods).Distinct())
					{
						// Vanilla methods are patched by many mods for unrelated reasons; listing those is noise.
						if (method.DeclaringType?.Assembly == vanilla) continue;

						// (Whether our own patches are still installed is PatchVerifier's job.)
						Patches info = Harmony.GetPatchInfo(method);
						if (info == null) continue;

						Collect(lines, fix, method, info.Prefixes, "prefix", ours, isekai,
							"runs before it and can skip it or change its inputs");
						Collect(lines, fix, method, info.Transpilers, "transpiler", ours, isekai,
							"rewrites its code and can undo or bypass this fix");
						Collect(lines, fix, method, info.Postfixes, "postfix", ours, isekai,
							"runs after it and can change its result");
						Collect(lines, fix, method, info.Finalizers, "finalizer", ours, isekai,
							"can swallow or replace its exceptions");
					}
				}

				if (lines.Count > 0)
				{
					FixLog.Warn("Other mods patch Isekai methods that these fixes change or rely on. They may alter what the fix does; "
						+ "if a fixed behaviour misbehaves, check these first:\n  " + string.Join("\n  ", lines.ToArray()));
				}
				else if (atStartup)
				{
					FixLog.Info("Conflict check: no other mod patches the Isekai methods these fixes change or rely on.");
				}
			}
			catch (Exception ex)
			{
				FixLog.Warn("conflict check failed: " + ex.Message);
			}
		}

		private static void Collect(List<string> lines, IsekaiFix fix, MethodBase method, IEnumerable<Patch> patches, string kind,
			Assembly ours, Assembly isekai, string impact)
		{
			if (patches == null) return;
			foreach (Patch p in patches)
			{
				Assembly owner = p.PatchMethod?.DeclaringType?.Assembly;
				if (owner == ours || owner == isekai) continue;
				string key = fix.Id + "|" + Key(method) + "|" + kind + "|" + p.owner + "|" + p.PatchMethod?.DeclaringType?.FullName + "." + p.PatchMethod?.Name;
				if (!reported.Add(key)) continue;
				lines.Add(fix.Label + ": " + ModName(owner) + " (Harmony id \"" + p.owner + "\") has a " + kind + " on "
					+ FixLog.Describe(method) + " [" + p.PatchMethod?.DeclaringType?.FullName + "." + p.PatchMethod?.Name
					+ ", priority " + p.priority + "] that " + impact + ".");
			}
		}

		private static string Key(MethodBase m) => m.DeclaringType?.FullName + "." + m.Name + "/" + m.GetParameters().Length;

		private static string ModName(Assembly asm)
		{
			if (asm == null) return "an unknown mod";
			foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
			{
				if (mod.assemblies?.loadedAssemblies != null && mod.assemblies.loadedAssemblies.Contains(asm))
					return "\"" + mod.Name + "\"";
			}
			return "assembly " + asm.GetName().Name;
		}
	}
}
