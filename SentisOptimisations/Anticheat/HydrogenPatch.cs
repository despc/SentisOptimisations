using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage.Game;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class HydrogenPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static ConcurrentDictionary<long, List<ulong>> _stockpileChangeDictionary = new ConcurrentDictionary<long, List<ulong>>();
        public static void Patch(PatchContext ctx)
        {
            var ChangeStockpileMode = typeof(MyGasTank).GetMethod
                ("ChangeStockpileMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(ChangeStockpileMode).Suffixes.Add(
                typeof(HydrogenPatch).GetMethod(nameof(ChangeStockpileModePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var ChangeEnabled = typeof(MyGasTank).GetMethod
                ("OnEnabledChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(ChangeEnabled).Suffixes.Add(
                typeof(HydrogenPatch).GetMethod(nameof(ChangeStockpileModePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
        }
        private static void ChangeStockpileModePatched(MyGasTank __instance)
        {
            try
            {
                List<ulong> tickList = new List<ulong>();
                if(_stockpileChangeDictionary.TryGetValue(__instance.EntityId, out tickList))
                {
                    if (tickList.Count > 10)
                    {
                        int cheatDelta = 0;
                        for (var i = 0; i < tickList.Count - 1; i++)
                        {
                            ulong delta = tickList[i + 1] - tickList[i];
                            if (delta < 11)
                            {
                                cheatDelta++;
                            }
                        }

                        if (cheatDelta > 5)
                        {
                            
                            var myFatBlockReader = __instance.CubeGrid.GetFatBlocks<MyProgrammableBlock>();
                            var isPam = false;
                            foreach (var myProgrammableBlock in myFatBlockReader)
                            {
                                var programData = ((IMyProgrammableBlock)myProgrammableBlock).ProgramData;
                                if (String.IsNullOrEmpty(programData))
                                {
                                    continue;
                                }
                                if (programData.Contains("Path Auto Miner"))
                                {
                                    isPam = true;
                                }
                            }

                            if (isPam)
                            {
                                return;
                            }
                            foreach (var myProgrammableBlock in myFatBlockReader)
                            {
                                
                                myProgrammableBlock.Enabled = false;
                                var mySlimBlock = myProgrammableBlock.SlimBlock;
                                mySlimBlock.DoDamage((float)(mySlimBlock.Integrity / 1.5), MyDamageType.Explosion, true, null, 0);
                                SentisOptimisationsPlugin.Log.Warn(myProgrammableBlock.OwnerId +
                                                                   " Try to cheat hydrogen!");
                                myProgrammableBlock.UpdateProgram("oh nononononono...");
                            }
                            __instance.ChangeFillRatioAmount(0);
                            _stockpileChangeDictionary.Remove(__instance.EntityId);
                            return;
                        }
                    }
                    tickList.Add(MySandboxGame.Static.SimulationFrameCounter);
                    return;
                }

                List<ulong> tickListNew = new List<ulong>();
                tickListNew.Add(MySandboxGame.Static.SimulationFrameCounter);
                _stockpileChangeDictionary[__instance.EntityId] = tickListNew;
                
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("InitPatched Exception ", e);
            }
        }
    }
}