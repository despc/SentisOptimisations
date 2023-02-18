using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using Torch.Utils;
using VRage.Game.ModAPI;
using VRage.Groups;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class ConnectorPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        [ReflectedMethodInfo(typeof (MyShipConnector), "CheckElectricalConstraints")]
        internal static readonly MethodInfo original;
        
        [ReflectedMethodInfo(typeof (ConnectorPatch), "CheckElectricalConstraintsPatched")]
        private static readonly MethodInfo patched;
        public static void Patch(PatchContext ctx)
        {
            ctx.GetPattern(original).Prefixes.Add(patched);
        }

        private static bool CheckElectricalConstraintsPatched(MyShipConnector __instance)
        {
            try
            {
                MethodInfo method1 = __instance.GetType().GetMethod("OnConstraintAdded", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo method2 = __instance.GetType().GetMethod("OnConstraintRemoved", BindingFlags.Instance | BindingFlags.NonPublic);
                MyGroupsBase<MyCubeGrid> groups = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Electrical);
                long entityId = __instance.EntityId;
                MyCubeGrid cubeGrid = __instance.CubeGrid;
                MyShipConnector other = __instance.Other;
                bool flag = groups.LinkExists(entityId, cubeGrid, other?.CubeGrid);
                if (__instance.IsTransferingElectricityCurrently)
                {
                    if (!flag)
                    {
                        method1.Invoke((object) __instance, new object[2]
                        {
                            (object) GridLinkTypeEnum.Electrical,
                            (object) other?.CubeGrid
                        });
                        if (other != null && !MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Electrical).LinkExists(entityId, other.CubeGrid, cubeGrid))
                            method1.Invoke((object) other, new object[2]
                            {
                                (object) GridLinkTypeEnum.Electrical,
                                (object) cubeGrid
                            });
                    }
                }
                else if (flag)
                {
                    method2.Invoke((object) __instance, new object[2]
                    {
                        (object) GridLinkTypeEnum.Electrical,
                        (object) other?.CubeGrid
                    });
                    if (other != null)
                        method2.Invoke((object) other, new object[2]
                        {
                            (object) GridLinkTypeEnum.Electrical,
                            (object) cubeGrid
                        });
                }
                groups.LinkExists(entityId, cubeGrid, other?.CubeGrid);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return false;
        }
    }
}