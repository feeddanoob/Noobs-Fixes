using System;
using Verse;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// Fixes for Isekai RPG Leveling (JellyCreative.IsekaiLeveling), applied as Harmony patches so
	/// the mod itself stays the unmodified Workshop copy. LoadFolders.xml only loads this assembly
	/// when Isekai is active; the check below is a second guard.
	/// </summary>
	[StaticConstructorOnStartup]
	public static class IsekaiLevelingFixMod
	{
		static IsekaiLevelingFixMod()
		{
			if (!IsekaiInstall.ModActive(IsekaiInstall.PackageId)) return;

			// Wait until every mod's [StaticConstructorOnStartup] has run: Isekai sets up its
			// RimWorld of Magic hooks there, and other mods patch there too. Patching and the
			// conflict check both need to see the final picture.
			LongEventHandler.ExecuteWhenFinished(() =>
			{
				try
				{
					FixRegistry.Run();
				}
				catch (Exception ex)
				{
					FixLog.Error("setup crashed; none of the Isekai fixes are active.\n" + ex);
				}
			});
		}
	}
}
