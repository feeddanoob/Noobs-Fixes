using System.Collections.Generic;
using System.Reflection;

namespace NoobsFixes.IsekaiLevelingFix
{
	/// <summary>
	/// One Isekai RPG Leveling method that a fix patches or relies on, named the way it appears in
	/// the assembly's metadata. Shared with the offline fingerprint tool
	/// (Source\IsekaiFingerprintTool), so nothing in this file may reference RimWorld or Harmony.
	/// </summary>
	internal sealed class Anchor
	{
		/// <summary>Full metadata type name; nested types are joined with '+'.</summary>
		public readonly string TypeName;

		/// <summary>Method name, or ".cctor" for a static constructor.</summary>
		public readonly string MethodName;

		/// <summary>Parameter count, or -1 when the name alone is unique on the type.</summary>
		public readonly int ParamCount;

		/// <summary>
		/// The old local fork has the same source for this method as upstream (its fix was written
		/// after the fork). The fingerprint tool then records the fork's build as more upstream
		/// code, not as "already fixed", so the fix also applies while the fork is loaded.
		/// </summary>
		public readonly bool SameCodeInFork;

		public Anchor(string typeName, string methodName, int paramCount = -1, bool sameCodeInFork = false)
		{
			TypeName = typeName;
			MethodName = methodName;
			ParamCount = paramCount;
			SameCodeInFork = sameCodeInFork;
		}

		/// <summary>Stable key used in the known-fingerprint tables.</summary>
		public string Key => ParamCount < 0
			? TypeName + "::" + MethodName
			: TypeName + "::" + MethodName + "/" + ParamCount;

		/// <summary>Short "Type.Method" form for log lines.</summary>
		public string Display
		{
			get
			{
				int dot = TypeName.LastIndexOf('.');
				return TypeName.Substring(dot + 1).Replace('+', '.') + "." + MethodName;
			}
		}

		public override string ToString() => Display;
	}

	/// <summary>Every Isekai method the fixes in this assembly depend on.</summary>
	internal static class Anchors
	{
		private const string NS = "IsekaiLeveling.";

		// ── Core ──
		public static readonly Anchor GainXP = new Anchor(NS + "IsekaiComponent", "GainXP");
		public static readonly Anchor FlushPendingXP = new Anchor(NS + "IsekaiComponent", "FlushPendingXP");
		public static readonly Anchor IsekaiCompTickRare = new Anchor(NS + "IsekaiComponent", "CompTickRare");
		public static readonly Anchor IsekaiPostSpawnSetup = new Anchor(NS + "IsekaiComponent", "PostSpawnSetup");
		public static readonly Anchor IsekaiLevelUp = new Anchor(NS + "IsekaiComponent", "LevelUp");
		public static readonly Anchor IsekaiDevAddLevel = new Anchor(NS + "IsekaiComponent", "DevAddLevel");
		public static readonly Anchor GetClassStatAffinity = new Anchor(NS + "IsekaiComponent", "GetClassStatAffinity");

		// ── Skill tree ──
		public static readonly Anchor ForceUnlockNode = new Anchor(NS + "SkillTree.PassiveTreeTracker", "ForceUnlockNode");
		public static readonly Anchor FindTreeForNode = new Anchor(NS + "SkillTree.PassiveTreeTracker", "FindTreeForNode");
		public static readonly Anchor FindNodeInAnyTree = new Anchor(NS + "SkillTree.PassiveTreeTracker", "FindNodeInAnyTree");
		public static readonly Anchor AutoSpendAccumulated = new Anchor(NS + "SkillTree.TreeAutoAssigner", "AutoSpendAccumulated");
		public static readonly Anchor AssignTreeProgression = new Anchor(NS + "SkillTree.TreeAutoAssigner", "AssignTreeProgression");
		public static readonly Anchor SpendPointsOnTree = new Anchor(NS + "SkillTree.TreeAutoAssigner", "SpendPointsOnTree");

		// ── Mob ranking ──
		public static readonly Anchor MobRankStatPatchesCctor = new Anchor(NS + "MobRanking.MobRankStatPatches", ".cctor");
		public static readonly Anchor GetRankStatFactor = new Anchor(NS + "MobRanking.MobRankStatPatches", "GetRankStatFactor");
		public static readonly Anchor TakeDamagePrefix = new Anchor(NS + "MobRanking.Thing_TakeDamage_Patch", "Prefix");
		public static readonly Anchor ReDeriveIfDifficultyChanged = new Anchor(NS + "MobRanking.MobRankComponent", "ReDeriveIfDifficultyChanged");
		public static readonly Anchor MobRankPostExposeData = new Anchor(NS + "MobRanking.MobRankComponent", "PostExposeData");
		public static readonly Anchor ProcessRaidPawns = new Anchor(NS + "MobRanking.RaidRankSystem", "ProcessRaidPawns");
		public static readonly Anchor AssignRaidPawnRank = new Anchor(NS + "MobRanking.RaidRankSystem", "AssignRaidPawnRank", 4);
		public static readonly Anchor GetEffectiveRaidPoints = new Anchor(NS + "MobRanking.RaidRankSystem", "GetEffectiveRaidPoints");
		public static readonly Anchor GetColonyBestRank = new Anchor(NS + "MobRanking.RaidRankSystem", "GetColonyBestRank");

		// ── XP / trait patches ──
		public static readonly Anchor SkillLearnXPPostfix = new Anchor(NS + "Patches.Patch_SkillLearnXP", "Postfix");
		public static readonly Anchor CombatXPMeleePostfix = new Anchor(NS + "Patches.Patch_CombatXP_Melee", "Postfix");
		public static readonly Anchor DamageTrackerGetAttackers = new Anchor(NS + "Patches.DamageTracker", "GetAttackers");
		public static readonly Anchor EchoOfDefeatPostfix = new Anchor(NS + "Patches.Patch_EchoOfDefeat_Downed", "Postfix");
		public static readonly Anchor CharacterCreationPostfix = new Anchor(NS + "Patches.Patch_CharacterCreation", "Postfix");
		public static readonly Anchor CharacterCreationDrawPanel = new Anchor(NS + "Patches.Patch_CharacterCreation", "DrawIsekaiPanel");

		// ── Quests / world boss / duels ──
		public static readonly Anchor GenerateMaterials = new Anchor(NS + "Quests.IncidentWorker_IsekaiHunt", "GenerateMaterials");
		public static readonly Anchor WorldHuntCheckLeftSite = new Anchor(NS + "Quests.QuestPart_IsekaiWorldHunt", "CheckIfColonistsLeftSite");
		public static readonly Anchor WorldHuntCleanup = new Anchor(NS + "Quests.QuestPart_IsekaiWorldHunt", "Cleanup");
		public static readonly Anchor HuntTrackerTick = new Anchor(NS + "Quests.IsekaiHuntTracker", "GameComponentTick");
		public static readonly Anchor WorldBossTryRemoveSite = new Anchor(NS + "WorldBoss.QuestPart_WorldBoss", "TryRemoveSite");
		public static readonly Anchor GuildDropZoneTryDeliver = new Anchor(NS + "Quests.GuildDropZoneUtility", "TryDeliver");
		public static readonly Anchor TitleDuelHandleArrival = new Anchor(NS + "TitleDuelTracker", "HandleArrival");

		// ── Effects / character forge ──
		public static readonly Anchor GetAuraState = new Anchor(NS + "Effects.AuraSystem", "GetAuraState");
		public static readonly Anchor ExportXml = new Anchor(NS + "CharacterForge.IsekaiCharacterForge", "ExportXml");

		// ── RimWorld of Magic compatibility ──
		public static readonly Anchor RoMMagicTickPostfix = new Anchor(NS + "Compatibility.RoMHarmonyPatches", "CompMagic_CompTick_Postfix");
		public static readonly Anchor RoMMightTickPostfix = new Anchor(NS + "Compatibility.RoMHarmonyPatches", "CompMight_CompTick_Postfix");
		public static readonly Anchor RoMApplyTrackedMultiplier = new Anchor(NS + "Compatibility.RoMHarmonyPatches", "ApplyTrackedMultiplier");
		public static readonly Anchor RoMChiPostfix = new Anchor(NS + "Compatibility.RoMHarmonyPatches", "Chi_CompPostTick_Postfix");
		public static readonly Anchor RoMSummonPostfix = new Anchor(NS + "Compatibility.RoMHarmonyPatches", "CompSummoned_PostSpawnSetup_Postfix");
		public static readonly Anchor RoMMagicDamageMultiplier = new Anchor(NS + "Compatibility.RoMCompatibility", "GetMagicDamageMultiplier");
		public static readonly Anchor RoMMightDamageMultiplier = new Anchor(NS + "Compatibility.RoMCompatibility", "GetMightDamageMultiplier");
		public static readonly Anchor RoMGetPawnFromAbility = new Anchor(NS + "Compatibility.RoMCompatibility", "GetPawnFromAbility");
		public static readonly Anchor RoMSetCooldownTicksLeft = new Anchor(NS + "Compatibility.RoMCompatibility", "SetCooldownTicksLeft");
		public static readonly Anchor RoMGetCooldownTicksLeft = new Anchor(NS + "Compatibility.RoMCompatibility", "GetCooldownTicksLeft");

		// ── Character Editor compatibility ──
		public static readonly Anchor CharacterEditorAutoDistribute = new Anchor(NS + "Compatibility.CharacterEditorCompatibility", "AutoDistributePoints", sameCodeInFork: true);

		// ── UI ──
		public static readonly Anchor SkillTreeNodeTooltip = new Anchor(NS + "UI.Window_SkillTree", "DrawNodeTooltip");
		public static readonly Anchor SkillTreeTreeSummary = new Anchor(NS + "UI.Window_SkillTree", "DrawDetailSection_TreeSummary");
		public static readonly Anchor SkillTreeSummaryLine = new Anchor(NS + "UI.Window_SkillTree", "DrawSummaryLine");
		public static readonly Anchor SkillTreeLoadTree = new Anchor(NS + "UI.Window_SkillTree", "LoadTree");
		public static readonly Anchor SkillTreeClassPanel = new Anchor(NS + "UI.Window_SkillTree", "DrawClassPanel");
		public static readonly Anchor RunicGetAllEquipment = new Anchor(NS + "Forge.Window_RunicStation", "GetAllEquipment");
		public static readonly Anchor RunicGetRuneItems = new Anchor(NS + "Forge.Window_RunicStation", "GetAvailableRuneItems");

		/// <summary>All anchors declared above.</summary>
		public static IEnumerable<Anchor> All()
		{
			foreach (FieldInfo f in typeof(Anchors).GetFields(BindingFlags.Public | BindingFlags.Static))
			{
				if (f.GetValue(null) is Anchor a) yield return a;
			}
		}
	}
}
