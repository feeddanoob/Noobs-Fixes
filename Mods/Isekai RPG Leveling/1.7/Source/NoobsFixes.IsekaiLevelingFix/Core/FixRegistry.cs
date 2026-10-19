using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Decides, fix by fix, whether to apply it, then reports the outcome in one log block.
	///
	///   Applied       — patched and running.
	///   Not needed    — doesn't apply to this mod list.
	///   Built in      — the loaded Isekai build already has this change (e.g. the local fork).
	///   PAUSED        — Isekai changed a method the fix relies on since the fix was checked.
	///                   Held back until re-verified, so an upstream update can't be overridden blindly.
	///   FAILED        — something it needs is missing or Harmony refused; nothing of it stays patched.
	/// </summary>
	internal static class FixRegistry
	{
		public const string HarmonyId = "com.feeddanoob.NoobEdit.IsekaiLevelingFix";

		public static Harmony Harmony { get; private set; }

		public static readonly List<IsekaiFix> Fixes = new List<IsekaiFix>
		{
			// Bug fixes
			new MeleeDamageOnceFix(),
			new GimmickCacheFix(),
			new XpBatchPerSourceFix(),
			new SkillLearnXpFix(),
			new AbsorbedHitXpFix(),
			new MedievalMaterialsFix(),
			new SiteRemovalFix(),
			new CharacterCreationFix(),
			new TitleDuelDiscardFix(),
			new DropZoneDeliveryFix(),
			new ExportXmlEscapeFix(),
			new LegacyRankNoRerollFix(),
			new AuraEvictionFix(),
			new AutoSpendLeftoverFix(),
			new RoMDamageFix(),
			new BonusColorFix(),
			new ClassStatRowsFix(),
			new CharacterEditorDistributeFix(),
			// Performance
			new NodeLookupFix(),
			new RoMReflectionCacheFix(),
			new AttackerLookupFix(),
			new EchoOfDefeatFieldFix(),
			new RaidRankOncePerRaidFix(),
			new RunicStationCacheFix(),
			new TreeLookupFix(),
			// Not ported, because the 2026-10-14 Isekai update fixed them itself:
			//   AutoDistributeByClass always gave single points to STR (upstream now keeps its place in the cycle);
			//   stats-window effect list recomputed every frame / cut off its last rows (upstream rewrote it).
			// (Their old fork numbers #23/#26 are retired; #26 is now the Character Editor fix.)
		};

		public static void Run()
		{
			Harmony = new Harmony(HarmonyId);
			IsekaiInstall.Locate();

			Dictionary<Anchor, string> fingerprints;
			string fingerprintProblem;
			try
			{
				fingerprints = ComputeFingerprints(out fingerprintProblem);
			}
			catch (Exception ex)
			{
				// e.g. the runtime's System.Reflection.Metadata could not be bound: every fix that
				// isn't marked safe then pauses instead of applying unchecked.
				fingerprints = new Dictionary<Anchor, string>();
				fingerprintProblem = "could not check Isekai's code (" + ex.GetType().Name + ": " + ex.Message + ")";
			}

			foreach (IsekaiFix fix in Fixes)
			{
				try
				{
					ApplyOne(fix, fingerprints, fingerprintProblem);
				}
				catch (Exception ex)
				{
					fix.Active = false;
					fix.Status = FixStatus.Failed;
					fix.Detail = "unexpected setup error: " + ex.Message;
					FixLog.Error(fix.Label + ": unexpected setup error\n" + ex);
				}
			}

			startupProblem = fingerprintProblem;
			InstallGameLoadHook();
			ReportStatus("at startup");
			ConflictScanner.Scan(atStartup: true);
		}

		private static string startupProblem;

		/// <summary>Verify every active fix is still really in place, then log the full status block.</summary>
		private static void ReportStatus(string when)
		{
			PatchVerifier.Result verified;
			try
			{
				verified = PatchVerifier.VerifyAll();
			}
			catch (Exception ex)
			{
				verified = new PatchVerifier.Result { Unverifiable = new List<string> { "verification itself failed: " + ex.Message } };
			}
			LogSummary(when, startupProblem, verified);
		}

		// Kept out of line so a failure to load System.Reflection.Metadata surfaces at this call, inside Run's try/catch.
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static Dictionary<Anchor, string> ComputeFingerprints(out string problem)
		{
			problem = null;
			var result = new Dictionary<Anchor, string>();
			if (IsekaiInstall.DllPath == null)
			{
				problem = "could not find IsekaiLeveling.dll on disk, so Isekai's code could not be checked against what the fixes expect";
				return result;
			}
			try
			{
				using (var fp = new MetadataFingerprinter(File.ReadAllBytes(IsekaiInstall.DllPath)))
				{
					foreach (Anchor a in Anchors.All())
					{
						if (fp.TryFingerprint(a, out string f, out _)) result[a] = f;
					}
				}
			}
			catch (Exception ex)
			{
				problem = "could not read " + IsekaiInstall.DllPath + " to check Isekai's code: " + ex.Message;
				result.Clear();
			}
			return result;
		}

		private static void ApplyOne(IsekaiFix fix, Dictionary<Anchor, string> fingerprints, string fingerprintProblem)
		{
			string notNeeded = fix.NotNeededReason();
			if (notNeeded != null)
			{
				Set(fix, FixStatus.NotNeeded, notNeeded);
				return;
			}

			// 1. Everything the fix relies on must still exist.
			var missing = new List<string>();
			foreach (Anchor a in fix.DependsOn)
			{
				var m = AnchorResolver.Resolve(a, out string error);
				if (m == null) missing.Add(error);
				else fix.AnchorMethods.Add(m);
			}
			if (missing.Count > 0)
			{
				Set(fix, FixStatus.Failed, string.Join("; ", missing));
				return;
			}

			// 2. Is Isekai's code still the code this fix was written against?
			bool anyAlreadyFixed = false;
			foreach (Anchor a in fix.DependsOn)
			{
				if (!fingerprints.TryGetValue(a, out string fp)) fix.ChangedAnchors.Add(a);
				else if (KnownFingerprints.IsUpstream(a, fp)) { }
				else if (KnownFingerprints.IsAlreadyFixed(a, fp)) anyAlreadyFixed = true;
				else fix.ChangedAnchors.Add(a);
			}
			if (fix.ChangedAnchors.Count == 0 && anyAlreadyFixed)
			{
				Set(fix, FixStatus.AlreadyFixed, "the loaded Isekai build already contains this change");
				return;
			}
			if (fix.ChangedAnchors.Count > 0 && !fix.SafeOnUpstreamChange)
			{
				Set(fix, FixStatus.Paused, fingerprintProblem != null
					? "could not verify Isekai's code (" + fingerprintProblem + ")"
					: "Isekai changed " + string.Join(", ", fix.ChangedAnchors.Select(a => a.Display).ToArray()) + " since this fix was checked");
				return;
			}

			// 3. Apply, all or nothing.
			var scope = new PatchScope(Harmony, fix);
			fix.Active = true;
			try
			{
				fix.Apply(scope);
				if (fix.ChangedAnchors.Count > 0)
					Set(fix, FixStatus.AppliedDespiteChange, "Isekai changed " + string.Join(", ", fix.ChangedAnchors.Select(a => a.Display).ToArray()) + "; applied anyway (this fix can't conflict with that), worth re-checking");
				else
					Set(fix, FixStatus.Applied, null);
			}
			catch (Exception ex)
			{
				fix.Active = false;
				scope.RollBack();
				Set(fix, FixStatus.Failed, ex is FixFailedException ? ex.Message : "unexpected error: " + PatchScope.Root(ex));
			}
		}

		private static void Set(IsekaiFix fix, FixStatus status, string detail)
		{
			fix.Status = status;
			fix.Detail = detail;
			if (status != FixStatus.Applied && status != FixStatus.AppliedDespiteChange) fix.Active = false;
		}

		private static string StatusWord(FixStatus s)
		{
			switch (s)
			{
				case FixStatus.Applied: return "applied   ";
				case FixStatus.AppliedDespiteChange: return "applied*  ";
				case FixStatus.NotNeeded: return "not needed";
				case FixStatus.AlreadyFixed: return "built in  ";
				case FixStatus.Paused: return "PAUSED    ";
				case FixStatus.Failed: return "FAILED    ";
				case FixStatus.Faulted: return "OFF (error)";
				case FixStatus.Disrupted: return "OFF (gone)";
				default: return s.ToString();
			}
		}

		private static bool IsProblem(FixStatus s)
			=> s == FixStatus.Paused || s == FixStatus.Failed || s == FixStatus.Faulted || s == FixStatus.Disrupted;

		private static void LogSummary(string when, string fingerprintProblem, PatchVerifier.Result verified)
		{
			int applied = Fixes.Count(f => f.Status == FixStatus.Applied || f.Status == FixStatus.AppliedDespiteChange);
			int builtIn = Fixes.Count(f => f.Status == FixStatus.AlreadyFixed);
			int notNeeded = Fixes.Count(f => f.Status == FixStatus.NotNeeded);
			var problems = Fixes.Where(f => IsProblem(f.Status)).ToList();

			var sb = new StringBuilder();
			sb.Append(FixLog.Tag).Append("Status ").Append(when).Append(": ").Append(IsekaiInstall.Describe()).Append('\n');
			sb.Append("  ").Append(Fixes.Count).Append(" fixes: ").Append(applied).Append(" active");
			if (builtIn > 0) sb.Append(", ").Append(builtIn).Append(" already built into this Isekai build");
			if (notNeeded > 0) sb.Append(", ").Append(notNeeded).Append(" not needed");
			if (problems.Count > 0) sb.Append(", ").Append(problems.Count).Append(" NOT active (see warning)");
			sb.Append(".\n");
			sb.Append("  Verified now: ").Append(verified.PatchesChecked).Append(" patches installed, ")
				.Append(verified.CodeEditsChecked).Append(" code edits present in the final compiled methods");
			if (verified.FixesSwitchedOff > 0) sb.Append(", ").Append(verified.FixesSwitchedOff).Append(" fix(es) switched off by this check");
			sb.Append(".  Isekai code checked against: ").Append(KnownFingerprints.Source).Append('\n');
			foreach (IsekaiFix f in Fixes)
			{
				sb.Append("  ").Append(StatusWord(f.Status)).Append("  #").Append(f.Id).Append(' ')
					.Append(f.Kind == FixKind.Performance ? "[perf] " : "").Append(f.Title);
				if (!string.IsNullOrEmpty(f.Detail)) sb.Append("  (").Append(f.Detail).Append(')');
				sb.Append('\n');
			}
			Log.Message(sb.ToString().TrimEnd());

			if (builtIn == Fixes.Count - notNeeded && builtIn > 0)
			{
				FixLog.Info("Every fix is already part of the loaded Isekai build, so Noob's Edits changed nothing. Expected while the local fork is the active copy.");
			}

			if (problems.Count > 0 || fingerprintProblem != null || verified.Unverifiable.Count > 0)
			{
				var w = new StringBuilder("Problems " + when + ":\n");
				if (fingerprintProblem != null) w.Append("  ").Append(fingerprintProblem).Append('\n');
				foreach (IsekaiFix f in problems)
				{
					w.Append("  ").Append(StatusWord(f.Status).Trim()).Append(" #").Append(f.Id).Append(' ').Append(f.Title).Append(": ").Append(f.Detail).Append('\n');
				}
				foreach (string u in verified.Unverifiable) w.Append("  could not verify ").Append(u).Append('\n');
				if (problems.Any(f => f.Status == FixStatus.Paused))
					w.Append("  PAUSED: Isekai RPG Leveling was updated and changed code the fix depends on. Isekai's own behaviour runs until the fix is re-checked against the update.\n");
				if (problems.Any(f => f.Status == FixStatus.Failed))
					w.Append("  FAILED: the fix could not be installed (a method it needs is missing, Harmony refused, or the code it edits isn't there). Nothing of it is patched.\n");
				if (problems.Any(f => f.Status == FixStatus.Faulted))
					w.Append("  OFF (error): the fix threw an error while running and switched itself off; the full error was logged when it happened.\n");
				if (problems.Any(f => f.Status == FixStatus.Disrupted))
					w.Append("  OFF (gone): part of the fix was removed or overwritten after it was installed (usually by another mod), so it switched itself off.\n");
				FixLog.Warn(w.ToString().TrimEnd());
			}
		}

		// ─────────────── Game load hook ───────────────

		private static bool rescanned;

		private static void InstallGameLoadHook()
		{
			try
			{
				Harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
					postfix: new HarmonyMethod(typeof(FixRegistry), nameof(FinalizeInit_Postfix)));
			}
			catch (Exception ex)
			{
				FixLog.Error("could not hook Game.FinalizeInit; per-save caches of the fixes will not be reset between saves: " + ex.Message);
			}
		}

		private static void FinalizeInit_Postfix()
		{
			foreach (IsekaiFix fix in Fixes)
			{
				if (!fix.Active) continue;
				try { fix.OnGameLoaded(); }
				catch (Exception ex) { fix.Fault(ex, "reset on game load"); }
			}
			// Same status block as at startup, re-verified, so it sits right next to this play session in the log.
			ReportStatus("at game load");
			// Some mods patch lazily (on first game load rather than at startup); look once more.
			if (!rescanned)
			{
				rescanned = true;
				ConflictScanner.Scan(atStartup: false);
			}
		}
	}
}
