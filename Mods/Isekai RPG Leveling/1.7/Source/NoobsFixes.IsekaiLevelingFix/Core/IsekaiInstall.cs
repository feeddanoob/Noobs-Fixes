using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>Where the running copy of Isekai RPG Leveling lives, and how to find its methods.</summary>
	internal static class IsekaiInstall
	{
		public const string PackageId = "JellyCreative.IsekaiLeveling";

		/// <summary>Isekai's own Harmony id. Fixed in its code, so it doesn't change with the mod-list ID.</summary>
		public const string HarmonyId = "JellyCreative.IsekaiLeveling";

		/// <summary>
		/// When a local copy and the Workshop copy of a mod share a packageId, RimWorld gives the
		/// Workshop copy the ID "&lt;packageId&gt;_steam". A mod counts as active under either ID.
		/// </summary>
		public static bool ModActive(string packageId) => ModsConfig.IsActive(packageId) || ModsConfig.IsActive(packageId + "_steam");

		private static bool IsIsekaiId(string id)
			=> string.Equals(id, PackageId, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(id, PackageId + "_steam", StringComparison.OrdinalIgnoreCase);

		public static Assembly Assembly => typeof(IsekaiLeveling.IsekaiComponent).Assembly;

		public static ModContentPack Mod { get; private set; }

		/// <summary>The IsekaiLeveling.dll file that was loaded, or null if it could not be found.</summary>
		public static string DllPath { get; private set; }

		public static string Describe()
		{
			if (Mod == null) return "Isekai RPG Leveling (mod folder not found)";
			string source = Mod.ModMetaData?.Source == ContentSource.SteamWorkshop ? "Steam Workshop" : "local Mods folder";
			string text = "Isekai RPG Leveling from the " + source + " (" + Mod.RootDir + ")";
			if (Mod.PackageId != null && Mod.PackageId.EndsWith("_steam", StringComparison.OrdinalIgnoreCase))
				text += ", active as " + Mod.PackageId + " because another copy with the same ID is installed";
			return text;
		}

		public static void Locate()
		{
			// The mod that actually loaded Isekai's assembly; the ID is only a fallback.
			Assembly asm = Assembly;
			Mod = LoadedModManager.RunningModsListForReading
				.FirstOrDefault(m => m.assemblies?.loadedAssemblies != null && m.assemblies.loadedAssemblies.Contains(asm))
				?? LoadedModManager.RunningModsListForReading.FirstOrDefault(m => IsIsekaiId(m.PackageId));
			DllPath = null;
			if (Mod == null) return;

			string fileName = Assembly.GetName().Name + ".dll";
			var folders = new List<string>();
			if (Mod.foldersToLoadDescendingOrder != null) folders.AddRange(Mod.foldersToLoadDescendingOrder);
			folders.Add(Mod.RootDir);
			foreach (string folder in folders)
			{
				string path = Path.Combine(Path.Combine(folder, "Assemblies"), fileName);
				if (File.Exists(path))
				{
					DllPath = path;
					return;
				}
			}
		}
	}

	/// <summary>Turns an <see cref="Anchor"/> into the live MethodBase of the loaded Isekai assembly.</summary>
	internal static class AnchorResolver
	{
		private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

		public static MethodBase Resolve(Anchor anchor, out string error)
		{
			error = null;
			Type type = IsekaiInstall.Assembly.GetType(anchor.TypeName, false);
			if (type == null)
			{
				error = "Isekai no longer has the type " + anchor.TypeName;
				return null;
			}
			if (anchor.MethodName == ".cctor")
			{
				if (type.TypeInitializer != null) return type.TypeInitializer;
				error = anchor.TypeName + " no longer has a static constructor";
				return null;
			}

			MethodBase found = null;
			int count = 0;
			foreach (MethodInfo m in type.GetMethods(All))
			{
				if (m.Name != anchor.MethodName) continue;
				if (anchor.ParamCount >= 0 && m.GetParameters().Length != anchor.ParamCount) continue;
				found = m;
				count++;
			}
			if (count == 1) return found;
			error = count == 0
				? "Isekai no longer has " + anchor.Display + (anchor.ParamCount >= 0 ? " with " + anchor.ParamCount + " parameters" : "")
				: anchor.Display + " now has " + count + " overloads";
			return null;
		}
	}

	/// <summary>
	/// Fingerprints of the anchors as they were when each fix was written and checked. Filled in
	/// by KnownFingerprints.Generated.cs, which Source\IsekaiFingerprintTool writes.
	/// </summary>
	internal static partial class KnownFingerprints
	{
		/// <summary>Anchor key → fingerprints of the upstream (Workshop) code the fixes were written against.</summary>
		public static readonly Dictionary<string, HashSet<string>> Upstream = new Dictionary<string, HashSet<string>>();

		/// <summary>Anchor key → fingerprints of a build that already contains the change (the local fork).</summary>
		public static readonly Dictionary<string, HashSet<string>> AlreadyFixed = new Dictionary<string, HashSet<string>>();

		public static string Source = "not generated";

		static KnownFingerprints()
		{
			Fill();
		}

		static partial void Fill();

		private static void Add(Dictionary<string, HashSet<string>> table, string key, string fingerprint)
		{
			if (!table.TryGetValue(key, out HashSet<string> set)) table[key] = set = new HashSet<string>();
			set.Add(fingerprint);
		}

		public static bool IsUpstream(Anchor a, string fp) => Upstream.TryGetValue(a.Key, out var s) && s.Contains(fp);

		public static bool IsAlreadyFixed(Anchor a, string fp) => AlreadyFixed.TryGetValue(a.Key, out var s) && s.Contains(fp);
	}
}
