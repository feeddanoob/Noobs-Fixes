using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Hands a fix the tools to patch, and remembers every patch it made so a failure halfway
	/// through can remove them all again. Every lookup throws <see cref="FixFailedException"/>
	/// with a readable reason instead of returning null.
	/// </summary>
	internal sealed class PatchScope
	{
		private readonly Harmony harmony;
		private readonly IsekaiFix fix;
		private readonly List<KeyValuePair<MethodBase, MethodInfo>> applied = new List<KeyValuePair<MethodBase, MethodInfo>>();

		public PatchScope(Harmony harmony, IsekaiFix fix)
		{
			this.harmony = harmony;
			this.fix = fix;
		}

		// ─────────────── Lookups ───────────────

		/// <summary>Runtime method for an anchor (already verified to exist before Apply runs).</summary>
		public MethodBase Target(Anchor anchor)
		{
			MethodBase m = AnchorResolver.Resolve(anchor, out string error);
			if (m == null) throw new FixFailedException(error);
			return m;
		}

		public MethodInfo Method(Type type, string name, Type[] args = null)
		{
			MethodInfo m = type == null ? null : AccessTools.DeclaredMethod(type, name, args);
			if (m == null) throw new FixFailedException("method " + (type?.Name ?? "?") + "." + name + " not found");
			return m;
		}

		public FieldInfo Field(Type type, string name)
		{
			FieldInfo f = type == null ? null : AccessTools.Field(type, name);
			if (f == null) throw new FixFailedException("field " + (type?.Name ?? "?") + "." + name + " not found");
			return f;
		}

		public AccessTools.FieldRef<T, F> FieldRef<T, F>(string name)
		{
			try
			{
				return AccessTools.FieldRefAccess<T, F>(name);
			}
			catch (Exception ex)
			{
				throw new FixFailedException("field " + typeof(T).Name + "." + name + " is not usable as " + typeof(F).Name + ": " + Root(ex).Message);
			}
		}

		public D Delegate<D>(MethodInfo method) where D : Delegate
		{
			try
			{
				return AccessTools.MethodDelegate<D>(method, null, false, null);
			}
			catch (Exception ex)
			{
				throw new FixFailedException("cannot call " + FixLog.Describe(method) + " (" + typeof(D).Name + "): " + Root(ex).Message);
			}
		}

		public Type Type(string fullName)
		{
			Type t = AccessTools.TypeByName(fullName);
			if (t == null) throw new FixFailedException("type " + fullName + " not found");
			return t;
		}

		public void Require(bool condition, string reason)
		{
			if (!condition) throw new FixFailedException(reason);
		}

		// ─────────────── Patching ───────────────

		public void Prefix(MethodBase target, string patchMethod, int priority = -1, string[] before = null)
			=> Patch(target, patchMethod, HarmonyPatchType.Prefix, priority, before);

		public void Postfix(MethodBase target, string patchMethod, int priority = -1, string[] before = null)
			=> Patch(target, patchMethod, HarmonyPatchType.Postfix, priority, before);

		/// <param name="markers">
		/// Helper methods of ours that the transpiler puts into the code. The verification at
		/// startup and on every game load checks that at least one of them is in the method's
		/// final compiled code, i.e. that no other mod's transpiler wrote over our change.
		/// </param>
		public void Transpiler(MethodBase target, string patchMethod, params MethodInfo[] markers)
		{
			Patch(target, patchMethod, HarmonyPatchType.Transpiler, -1, null);
			if (markers != null && markers.Length > 0)
				fix.IlMarkers.Add(new KeyValuePair<MethodBase, MethodInfo[]>(target, markers));
		}

		public void Finalizer(MethodBase target, string patchMethod)
			=> Patch(target, patchMethod, HarmonyPatchType.Finalizer, -1, null);

		private void Patch(MethodBase target, string patchMethod, HarmonyPatchType type, int priority, string[] before)
		{
			if (target == null) throw new FixFailedException("patch target is missing");
			MethodInfo patch = AccessTools.DeclaredMethod(fix.GetType(), patchMethod);
			if (patch == null) throw new InvalidOperationException("patch method " + fix.GetType().Name + "." + patchMethod + " does not exist");

			var hm = new HarmonyMethod(patch);
			if (priority >= 0) hm.priority = priority;
			if (before != null) hm.before = before;

			if (type == HarmonyPatchType.Transpiler) TranspileReport.BeginSetup(fix);
			try
			{
				harmony.Patch(target,
					prefix: type == HarmonyPatchType.Prefix ? hm : null,
					postfix: type == HarmonyPatchType.Postfix ? hm : null,
					transpiler: type == HarmonyPatchType.Transpiler ? hm : null,
					finalizer: type == HarmonyPatchType.Finalizer ? hm : null);
			}
			catch (Exception ex)
			{
				throw new FixFailedException("Harmony could not patch " + FixLog.Describe(target) + ": " + Root(ex).Message, ex);
			}
			finally
			{
				if (type == HarmonyPatchType.Transpiler) TranspileReport.EndSetup();
			}

			applied.Add(new KeyValuePair<MethodBase, MethodInfo>(target, patch));
			fix.InstalledPatches.Add(new KeyValuePair<MethodBase, MethodInfo>(target, patch));
			if (!fix.PatchedMethods.Contains(target)) fix.PatchedMethods.Add(target);

			if (type == HarmonyPatchType.Transpiler)
			{
				string problem = TranspileReport.TakeSetupFailure();
				if (problem != null) throw new FixFailedException(problem);
			}
		}

		/// <summary>Removes every patch this scope made, then lets the fix undo anything else.</summary>
		public void RollBack()
		{
			for (int i = applied.Count - 1; i >= 0; i--)
			{
				try { harmony.Unpatch(applied[i].Key, applied[i].Value); }
				catch (Exception ex) { FixLog.Warn(fix.Label + ": could not remove its patch on " + FixLog.Describe(applied[i].Key) + " while rolling back: " + Root(ex).Message); }
			}
			applied.Clear();
			fix.PatchedMethods.Clear();
			fix.InstalledPatches.Clear();
			fix.IlMarkers.Clear();
			try { fix.Revert(); }
			catch (Exception ex) { FixLog.Warn(fix.Label + ": cleanup after rollback failed: " + Root(ex).Message); }
		}

		internal static Exception Root(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
			return ex;
		}
	}

	/// <summary>
	/// Transpilers in this assembly report whether they found the code they change. During setup
	/// a miss fails the fix. Later, Harmony re-runs every transpiler on a method whenever any mod
	/// patches that method again; a miss then means another mod's change got there first, and it
	/// is logged so the lost fix is not silent.
	/// </summary>
	internal static class TranspileReport
	{
		private static IsekaiFix settingUp;
		private static string setupFailure;
		private static readonly HashSet<string> warned = new HashSet<string>();

		/// <summary>True while PatchVerifier re-runs transpilers to read final code; it reports problems itself.</summary>
		public static bool Verifying;

		public static void BeginSetup(IsekaiFix fix)
		{
			settingUp = fix;
			setupFailure = null;
		}

		public static void EndSetup() => settingUp = null;

		public static string TakeSetupFailure()
		{
			string s = setupFailure;
			setupFailure = null;
			return s;
		}

		public static void Result(IsekaiFix fix, MethodBase original, bool found, string whatWasExpected)
		{
			if (found) return;
			string problem = "could not find " + whatWasExpected + " in " + FixLog.Describe(original);
			if (settingUp == fix)
			{
				setupFailure = setupFailure == null ? problem : setupFailure + "; " + problem;
				return;
			}
			if (Verifying) return;
			if (warned.Add(fix.Id + "|" + FixLog.Describe(original)))
			{
				FixLog.Warn(fix.Label + ": Harmony rebuilt " + FixLog.Describe(original) + " (another mod patched it) and this fix " + problem
					+ ". That part of the fix is no longer applied. The conflict report below or above names the other mod.");
			}
		}
	}
}
