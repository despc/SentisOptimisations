using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using SentisGameplayImprovements.AllGridsActions;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisGameplayImprovements.Observer.Utils;

public class SearchEntitiesUtils
{
    public static List<IMyVoxelMap> FindAllVoxelMapsInSphere(Vector3D center, int radius)
    {
        return EntitiesObserver.VoxelMaps.Where(map => Vector3D.Distance(map.GetPosition(), center) < radius)
            .ToList();
    }

    public static List<IMyEntity> FindAnythingInSphere(Vector3D center, int radius)
    {
        List<IMyEntity> result = new List<IMyEntity>();
        EntitiesObserver.Planets.Where(planet =>
                Vector3D.Distance(planet.PositionComp.GetPosition(), center) +
                Vector3D.Distance(planet.PositionComp.GetPosition(), planet.GetClosestSurfacePointGlobal(center)) <
                radius)
            .ForEach(map => result.Add(map));

        EntitiesObserver.VoxelMaps
            .Where(map => Vector3D.Distance(map.GetPosition(), center) + map.LocalVolume.Radius < radius)
            .ForEach(map => result.Add(map));

        EntitiesObserver.Safezones.Where(sz =>
                Vector3D.Distance(sz.PositionComp.GetPosition(), center) +
                (sz.Shape == MySafeZoneShape.Sphere ? sz.Radius : sz.Size.Max()) < radius)
            .ForEach(map => result.Add(map));
        EntitiesObserver.MyCubeGrids.Where(cg =>
                Vector3D.Distance(cg.PositionComp.GetPosition(), center) + cg.PositionComp.LocalVolume.Radius < radius)
            .ForEach(map => result.Add(map));

        return result;
    }
}