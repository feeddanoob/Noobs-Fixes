using HarmonyLib;

namespace Noob_s_Fixes
{
    public class HarmonyPatches
    {
        private static Harmony _harmonyInstance;
        public static Harmony HarmonyInstance { get { return _harmonyInstance; } }

        static HarmonyPatches()
        {
            _harmonyInstance = new Harmony("com.feeddanoob.NoobEdit");
            _harmonyInstance.PatchAll();
        }
    }
}
