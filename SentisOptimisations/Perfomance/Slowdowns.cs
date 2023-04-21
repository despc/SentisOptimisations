using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class Slowdowns
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Dictionary<long, int> CooldownsMyThrust = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMySensorBlock = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyGasGen = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyShipController = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyBattery = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyConveyorConnector = new Dictionary<long, int>();
        
        public static Dictionary<long, int> CooldownsMyCharacterAfter = new Dictionary<long, int>();
        public static Dictionary<long, int> CooldownsMyCharacterBefore = new Dictionary<long, int>();

        public static void Patch(PatchContext ctx)
        {
            var MethodCheckIsWorking = typeof(MyThrust).GetMethod
                ("CheckIsWorking", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodCheckIsWorking).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(CheckIsWorkingPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodSensorAfterSimulation10 = typeof(MySensorBlock).GetMethod
                ("UpdateAfterSimulation10", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MethodSensorAfterSimulation10).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MySensorBlockUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var GasGeneratorUpdateAfterSimulation100 = typeof(MyGasGenerator).GetMethod
                ("UpdateAfterSimulation100", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(GasGeneratorUpdateAfterSimulation100).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyGasGeneratorUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyShipControllerUpdateAfterSimulation = typeof(MyShipController).GetMethod
                ("UpdateAfterSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyShipControllerUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyShipControllerUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyBatteryBlockUpdateAfterSimulation = typeof(MyBatteryBlock).GetMethod
                ("UpdateAfterSimulation100", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyBatteryBlockUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyBatteryBlockUpdatePatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyConveyorConnectorUpdateAfterSimulation = typeof(MyConveyorConnector).GetMethod
                ("UpdateBeforeSimulation100", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyConveyorConnectorUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyConveyorConnectorPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyCharacterUpdateAfterSimulation = typeof(MyCharacter).GetMethod
                ("UpdateAfterSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyCharacterUpdateAfterSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyCharacterUpdateAfterSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyCharacterUpdateBeforeSimulation = typeof(MyCharacter).GetMethod
                ("UpdateBeforeSimulation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MyCharacterUpdateBeforeSimulation).Prefixes.Add(
                typeof(Slowdowns).GetMethod(nameof(MyCharacterUpdateBeforeSimulationPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool MyCharacterUpdateBeforeSimulationPatched(MyCharacter __instance)
        {
            var isClientOnline = __instance.IsClientOnline;
            if (isClientOnline != null && isClientOnline.Value)
            {
                return true;
            }

            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                if (NeedSkip(__instance.EntityId, 100, CooldownsMyCharacterBefore))
                {
                    return false;
                }
            }
            return true;
        }
        
        private static bool MyCharacterUpdateAfterSimulationPatched(MyCharacter __instance)
        {
            var isClientOnline = __instance.IsClientOnline;
            if (isClientOnline != null && isClientOnline.Value)
            {
                return true;
            }

            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                if (NeedSkip(__instance.EntityId, 100, CooldownsMyCharacterAfter))
                {
                    return false;
                }
            }
            return true;
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
        
        private static bool MyShipControllerUpdatePatched(MyShipController __instance)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMyShipController))
                    {
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMyShipController))
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
        
        private static bool MySensorBlockUpdatePatched(MySensorBlock __instance)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMySensorBlock))
                    {
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMySensorBlock))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        
        private static bool CheckIsWorkingPatched(MyThrust __instance, ref bool __result)
        {
            var blockId = __instance.EntityId;
            if (SentisOptimisationsPlugin.Config.SlowdownEnabled && MySandboxGame.Static.SimulationFrameCounter > 6000)
            {
                var myUpdateTiersPlayerPresence = __instance.CubeGrid.PlayerPresenceTier;
                if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier1)
                {
                    if (NeedSkip(blockId, 10, CooldownsMyThrust))
                    {
                        __result = false;
                        return false;
                    }
                }
                else if (myUpdateTiersPlayerPresence == MyUpdateTiersPlayerPresence.Tier2)
                {
                    if (NeedSkip(blockId, 100, CooldownsMyThrust))
                    {
                        __result = false;
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

            cooldowns[blockId] = 0;
            return true;
        }
    }
}