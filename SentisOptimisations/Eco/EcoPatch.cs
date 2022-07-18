using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Sync;
using HarmonyLib;
using NAPI;
using NLog.Fluent;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisOptimisationsPlugin.EcoPatch
{
    [PatchShim]
    public static class EcoPatch
    {
        // private static Harmony harmony = new Harmony("SentisOptimisationsPlugin.CrashFix");

        // private static MethodInfo original = typeof(Sync<MyTurretTargetFlags, SyncDirection.BothWays>).GetMethod
        //     ("IsValid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        // private static MethodInfo prefix = typeof(CrashFixPatch).GetMethod(nameof(MethodIsValidPatched),
        //     BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
        public static void Patch(PatchContext ctx)
        {
            
            Assembly ass = typeof(MySessionComponentEconomy).Assembly;
            Type MyStationGeneratorType = ass.GetType("Sandbox.Game.World.Generator.MyStationGenerator");
            var MethodGenerateStationsForFaction = MyStationGeneratorType.GetMethod
                ("GenerateStationsForFaction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            
            ctx.GetPattern(MethodGenerateStationsForFaction).Prefixes.Add(
                typeof(EcoPatch).GetMethod(nameof(GenerateStationsForFactionPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            
            var CanRevertCurrentMethod = typeof(MySessionComponentTrash).GetMethod(
                "VoxelRevertor_CanRevertCurrent", BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(CanRevertCurrentMethod).Prefixes.Add(
                typeof(EcoPatch).GetMethod(nameof(VoxelRevertorCanRevertCurrentPatch),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            
            // var MethodUpdateBeforeSimulation10 = typeof(MyShipDrill).GetMethod
            //     (nameof(MyShipDrill.UpdateBeforeSimulation10), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            //
            //
            // ctx.GetPattern(MethodUpdateBeforeSimulation10).Prefixes.Add(
            //     typeof(CrashFixPatch).GetMethod(nameof(UpdateBeforeSimulation10Patched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
 
            // harmony.Patch(original, new HarmonyMethod(prefix));

        }

        private static bool VoxelRevertorCanRevertCurrentPatch(MySessionComponentTrash __instance, ref bool __result)
        {
            var voxelBase = __instance.easyGetField("m_voxel_CurrentBase");
            if (voxelBase is MyVoxelMap && ((MyVoxelMap) voxelBase).Name != null && ((MyVoxelMap) voxelBase).Name.Contains("FieldAster"))
            {
                SentisOptimisationsPlugin.Log.Error("skip FieldAster" + ((MyVoxelMap) voxelBase).Name);
                __result = false;
                return false;
            }
            return true;
        }

        private static bool GenerateStationsForFactionPatched(bool required,
            object stationCounts,
            HashSet<Vector3D> usedLocations, object __instance, ref bool __result)
        {
            
            
            Assembly ass = typeof(MySessionComponentEconomy).Assembly;
            Type MyStationCountsPerFactionType = ass.GetType("Sandbox.Game.World.Generator.MyStationGenerator")
                .GetNestedType("MyStationCountsPerFaction",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            object missings = Activator.CreateInstance(MyStationCountsPerFactionType);
            
            List<MyPlanet> planets = new List<MyPlanet>();
            var planetNames = SentisOptimisationsPlugin.Config.PlanetsWithEco.Split(',');
            foreach (MyEntity entity in MyEntities.GetEntities())
            {
                if (entity is MyPlanet myPlanet)
                {
                    foreach (var planetName in planetNames)
                    {
                        if (myPlanet.Name.Contains(planetName))
                        {
                            planets.Add(myPlanet);
                        }
                    }
                }
            }
            bool someAsteroids = (double) MySession.Static.Settings.ProceduralDensity > 0.0;
            bool somePlanets = planets.Count > 0;
            var Outposts = (int)stationCounts.easyCallMethod("Outpost", new object []{required});
            if (Outposts > 0 & somePlanets)
                __instance.easyCallMethod("GenerateOutposts", new []{required, stationCounts, usedLocations, missings, planets});
            var OrbitStations = (int)stationCounts.easyCallMethod("Orbit", new object []{required});
            if (OrbitStations > 0 & somePlanets)
                __instance.easyCallMethod("GenerateOrbitalStations", new []{required, stationCounts, usedLocations, missings, planets});
            var MininsStations = (int)stationCounts.easyCallMethod("Mining", new object []{required});
            if (MininsStations > 0 & somePlanets)
                __instance.easyCallMethod("GenerateMiningStations", new []{required, stationCounts, usedLocations, missings});
            var SpaceStations = (int)stationCounts.easyCallMethod("Station", new object []{required});
            if (SpaceStations > 0 & somePlanets)
                __instance.easyCallMethod("GenerateSpaceStations", new []{required, stationCounts, usedLocations, missings, someAsteroids, somePlanets});
            SentisOptimisationsPlugin.Log.Warn("Created Outposts " + Outposts + " OrbitStations " + OrbitStations + " MininsStations " +
                     MininsStations + " SpaceStations " + SpaceStations);
            __result = true;
            return false;
        }
       
    }
}