using System;
using System.Collections.Generic;
using System.Reflection;

namespace NoobsFixes.IsekaiLevelingFix
{
	internal enum FixKind
	{
		Bug,
		Performance,
	}

	internal enum FixStatus
	{
		Pending,
		/// <summary>Patched and running.</summary>
		Applied,
		/// <summary>Patched even though Isekai changed a method it relies on (only for fixes marked safe for that).</summary>
		AppliedDespiteChange,
		/// <summary>Does not apply to this mod list (e.g. RimWorld of Magic not active).</summary>
		NotNeeded,
		/// <summary>The loaded Isekai build already contains this change (the local fork, or upstream fixed it).</summary>
		AlreadyFixed,
		/// <summary>Isekai changed code this fix relies on since it was written; held back until re-verified.</summary>
		Paused,
		/// <summary>Could not be applied; nothing of it is left patched.</summary>
		Failed,
		/// <summary>Was applied but threw at runtime; switched itself off, Isekai's own behaviour is back.</summary>
		Faulted,
		/// <summary>Was applied, but a later check found part of it gone (patch removed / code edit overwritten); switched off.</summary>
		Disrupted,
	}

	internal sealed class FixFailedException : Exception
	{
		public FixFailedException(string message, Exception inner = null) : base(message, inner) { }
	}

	/// <summary>
	/// One self-contained change to Isekai RPG Leveling. A fix is applied all-or-nothing: if any
	/// of its patches fails, every patch it already made is removed again. Patch bodies check
	/// <see cref="Active"/> and step aside when it is false, so a fix that faults at runtime hands
	/// control straight back to Isekai's original code.
	/// </summary>
	internal abstract class IsekaiFix
	{
		/// <summary>Two-digit number used in log lines.</summary>
		public abstract string Id { get; }

		public abstract string Title { get; }

		public abstract FixKind Kind { get; }

		/// <summary>Isekai methods this fix patches or whose behaviour it assumes.</summary>
		public abstract Anchor[] DependsOn { get; }

		/// <summary>
		/// If Isekai changes one of the anchors: false (default) pauses the fix until it is
		/// re-checked; true keeps it running with a warning. Only for fixes that cannot conflict
		/// with whatever upstream might have changed.
		/// </summary>
		public virtual bool SafeOnUpstreamChange => false;

		/// <summary>Non-null when the fix does not apply to this mod list.</summary>
		public virtual string NotNeededReason() => null;

		public abstract void Apply(PatchScope scope);

		/// <summary>Undo anything Apply did besides Harmony patches (those are removed for you).</summary>
		public virtual void Revert() { }

		/// <summary>Called after every new game or load (Game.FinalizeInit) to drop per-save state.</summary>
		public virtual void OnGameLoaded() { }

		/// <summary>Called when the fix switches itself off (runtime error, or a check found part of it gone).</summary>
		protected virtual void OnFaulted() { }

		/// <summary>
		/// Extra check run with the patch verification (startup and every game load) for changes
		/// that aren't Harmony patches. Returns a problem description, or null when all is well.
		/// </summary>
		public virtual string VerifyExtra() => null;

		public volatile bool Active;
		public FixStatus Status = FixStatus.Pending;
		public string Detail;
		internal readonly List<MethodBase> AnchorMethods = new List<MethodBase>();
		internal readonly List<MethodBase> PatchedMethods = new List<MethodBase>();
		internal readonly List<Anchor> ChangedAnchors = new List<Anchor>();

		/// <summary>Every Harmony patch this fix installed: (patched method, our patch method).</summary>
		internal readonly List<KeyValuePair<MethodBase, MethodInfo>> InstalledPatches = new List<KeyValuePair<MethodBase, MethodInfo>>();

		/// <summary>For each transpiled method, helper methods of ours that must appear in its final compiled code.</summary>
		internal readonly List<KeyValuePair<MethodBase, MethodInfo[]>> IlMarkers = new List<KeyValuePair<MethodBase, MethodInfo[]>>();

		public string Label => "#" + Id + " " + Title;

		/// <summary>Switches the fix off because a check found part of it missing. Isekai's own behaviour takes over.</summary>
		public void Disrupt(string reason)
		{
			if (!Active) return;
			Active = false;
			Status = FixStatus.Disrupted;
			Detail = reason;
			try { OnFaulted(); } catch { /* already reporting a failure */ }
			FixLog.Warn(Label + ": " + reason + ". The fix has switched itself off for this session and Isekai's original behaviour is back in place.");
		}

		/// <summary>
		/// Called from a patch body's catch block. Logs once, switches the fix off and leaves
		/// Isekai's own behaviour in place for the rest of the session.
		/// </summary>
		public void Fault(Exception ex, string where)
		{
			if (!Active) return;
			Active = false;
			Status = FixStatus.Faulted;
			Detail = "error in " + where;
			try { OnFaulted(); } catch { /* already reporting a failure */ }
			FixLog.Error(Label + ": error in " + where + ". The fix has switched itself off for this session and Isekai's original behaviour is back in place.\n" + ex);
		}
	}
}
