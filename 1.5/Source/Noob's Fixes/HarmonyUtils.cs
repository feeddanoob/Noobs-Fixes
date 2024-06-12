using HarmonyLib;
using Verse;

namespace Noob_s_Fixes
{
    internal class HarmonyUtils
    {
        public static bool IsSOS2SpaceMap(Map map)
        {
            var traverse = Traverse.Create(map);
            var isSpaceMethod = traverse.Method("IsSpace");
            if (isSpaceMethod.MethodExists() && (bool)isSpaceMethod.GetValue())
            {
                return true;
            }
            else if (map.Biome.defName.Contains("OuterSpace"))
            {
                return true;
            }
            return false;
        }
    }
}
