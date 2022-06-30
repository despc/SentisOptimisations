using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;

namespace SentisOptimisationsPlugin.ShipTool
{
    public class FuckWelderProcessor
    {
        public static Dictionary<long, Dictionary<long, int>> WelderDamageAccumulator =
            new Dictionary<long, Dictionary<long, int>>();

        public void Process()
        {
            var coolingSpeed = SentisOptimisationsPlugin.Config.CoolingSpeed;

            try
            {
                var gridDamageDatas = new Dictionary<long, Dictionary<long, int>>();
                foreach (var keyValuePair in WelderDamageAccumulator)
                {
                    if (gridDamageDatas.ContainsKey(keyValuePair.Key))
                    {
                        continue;
                    }
                    gridDamageDatas.Add(keyValuePair.Key, keyValuePair.Value);
                }
                
                foreach (var gridDamageData in gridDamageDatas)
                {
                    var gridId = gridDamageData.Key;
                    MyCubeGrid grid = (MyCubeGrid) MyEntities.GetEntityById(gridId);
                    if (grid == null)
                    {
                        continue;
                    }

                    Task.Run(() =>
                    {
                        try
                        {
                            var weldersIds = grid.GetFatBlocks().Where(block => block is MyShipWelder)
                                .Select(block => block.EntityId).ToHashSet();

                            Dictionary<long, int> welderDamageDatas = gridDamageData.Value;
                            var damageDatas = new Dictionary<long, int>();
                            foreach (var keyValuePair in welderDamageDatas)
                            {
                                if (damageDatas.ContainsKey(keyValuePair.Key))
                                {
                                    continue;
                                }
                                damageDatas.Add(keyValuePair.Key, keyValuePair.Value);
                            }
                            foreach (var welderDamageData in damageDatas)
                            {
                                long welderId = welderDamageData.Key;
                                if (welderDamageData.Value > coolingSpeed)
                                {
                                    welderDamageDatas[welderId] = welderDamageData.Value - coolingSpeed;
                                }
                                else
                                {
                                    welderDamageDatas[welderId] = 0;
                                }

                                weldersIds.Remove(welderId);
                            }

                            foreach (var welder in weldersIds)
                            {
                                welderDamageDatas[welder] = 0;
                            }
                        }
                        catch (Exception e)
                        {
                            SentisOptimisationsPlugin.Log.Error("FuckWelderProcessor exception ", e);
                            WelderDamageAccumulator.Clear();
                        }
                    });
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error("FuckWelderProcessor exception ", e);
                WelderDamageAccumulator.Clear();
            }
        }
    }
}