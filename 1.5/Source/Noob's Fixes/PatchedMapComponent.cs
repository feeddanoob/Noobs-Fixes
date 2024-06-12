using System.Linq;
using Verse;

namespace Noob_s_Fixes
{
    internal class PatchedMapComponent : MapComponent
    {
        public PatchedMapComponent(Map map) : base(map)
        {
        }
        public override void MapGenerated()
        {
            base.MapGenerated();
            var allMaps = Find.Maps.ToList();

            foreach (var map in allMaps)
            {
                if (HarmonyUtils.IsSOS2SpaceMap(map))
                {
                    var thingsOilList = map.listerThings.AllThings.Where(t => t.ThingID.Contains("OilWellHead")).ToList();
                    Log.Warning("SOS2 space map detected. Removing Rimefeller OilWells. Removed:" + thingsOilList.Count);
                    foreach (var thingOil in thingsOilList)
                    {
                        Log.Warning("Removing OilWell " + thingOil.ThingID);
                        thingOil.DeSpawn();
                    }
                }
            }
        }
    }
}
