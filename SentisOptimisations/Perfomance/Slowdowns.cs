using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class Slowdowns
    {
        public static Dictionary<long, int> CooldownsMyGasGen = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyBattery = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyConveyorConnector = new Dictionary<long, int>();

        public static readonly Random r = new Random();

        public static void Patch(PatchContext ctx)
        {
            var GasGeneratorUpdateAfterSimulation100 = typeof(MyGasGenerator).GetMethod
            ("UpdateAfterSimulation100",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(GasGeneratorUpdateAfterSimulation100).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyGasGeneratorUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MyBatteryBlockUpdateAfterSimulation = typeof(MyBatteryBlock).GetMethod
            ("UpdateAfterSimulation100",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyBatteryBlockUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyBatteryBlockUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

            var MyConveyorConnectorUpdateAfterSimulation = typeof(MyConveyorConnector).GetMethod
            ("UpdateBeforeSimulation100",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyConveyorConnectorUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyConveyorConnectorPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MyConveyorConnectorPatched(MyConveyorConnector __instance)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMyConveyorConnector))
                    {
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMyConveyorConnector))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool MyBatteryBlockUpdatePatched(MyBatteryBlock __instance)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMyBattery))
                    {
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMyBattery))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool MyGasGeneratorUpdatePatched(MyGasGenerator __instance)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMyGasGen))
                    {
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMyGasGen))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool NeedSkip(long blockId, int cd, Dictionary<long, int> cooldowns)
        {
            int cooldown;
            if (cooldowns.TryGetValue(blockId, out cooldown))
            {
                if (cooldown > cd)
                {
                    cooldowns[blockId] = 0;
                    return false;
                }

                cooldowns[blockId] = cooldown + 1;
                return true;
            }

            cooldowns[blockId] = r.Next(0, cd);
            return true;
        }
    }
}