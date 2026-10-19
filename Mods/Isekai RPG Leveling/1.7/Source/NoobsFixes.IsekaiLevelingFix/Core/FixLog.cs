using System.Reflection;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>Every line this assembly writes to the RimWorld log starts with the same tag, so it is easy to search for.</summary>
	internal static class FixLog
	{
		public const string Tag = "[Noob's Edits: Isekai fixes] ";

		public static void Info(string text) => Log.Message(Tag + text);

		public static void Warn(string text) => Log.Warning(Tag + text);

		public static void Error(string text) => Log.Error(Tag + text);

		/// <summary>Logs an error only the first time <paramref name="key"/> is seen this session.</summary>
		public static void ErrorOnce(string text, string key) => Log.ErrorOnce(Tag + text, StableKey(key));

		/// <summary>"Type.Method" for log lines.</summary>
		public static string Describe(MethodBase m)
		{
			if (m == null) return "(null)";
			return (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
		}

		private static int StableKey(string key)
		{
			unchecked
			{
				int h = (int)2166136261;
				string k = "NoobsIsekaiFix:" + key;
				for (int i = 0; i < k.Length; i++)
				{
					h ^= k[i];
					h *= 16777619;
				}
				return h;
			}
		}
	}
}
