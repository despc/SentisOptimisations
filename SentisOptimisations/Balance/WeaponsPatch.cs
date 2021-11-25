using System;
using System.Reflection;
using NLog;
using Sandbox.Definitions;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public class WeaponsPatch
    {
        
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            var MissileInit = typeof(MyMissileAmmoDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(MissileInit).Suffixes.Add(
                typeof(WeaponsPatch).GetMethod(nameof(MissileInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var LargeTurretBaseInit = typeof(MyLargeTurretBaseDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            ctx.GetPattern(LargeTurretBaseInit).Suffixes.Add(
                typeof(WeaponsPatch).GetMethod(nameof(LargeTurretBaseInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MyWeaponBlockDefinitionInit = typeof(MyWeaponBlockDefinition).GetMethod
                ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            
            ctx.GetPattern(MyWeaponBlockDefinitionInit).Suffixes.Add(
                typeof(WeaponsPatch).GetMethod(nameof(WeaponDefinitionInitPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            //
            // var ProjectileAmmoDefinitionInit = typeof(MyProjectileAmmoDefinition).GetMethod
            //     ("Init", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            //
            // ctx.GetPattern(ProjectileAmmoDefinitionInit).Suffixes.Add(
            //     typeof(WeaponsPatch).GetMethod(nameof(ProjectileAmmoDefinitionInitPatched),
            //         BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));

        }

        //
        private static void MissileInitPatched(MyMissileAmmoDefinition __instance)
        {
            try
            {
                __instance.MissileInitialSpeed = SentisOptimisationsPlugin.Config.MissileInitialSpeed;
                __instance.DesiredSpeed = SentisOptimisationsPlugin.Config.MissileInitialSpeed;
                __instance.MissileAcceleration = SentisOptimisationsPlugin.Config.MissileAcceleration;
                __instance.MissileExplosionDamage = SentisOptimisationsPlugin.Config.MissileDamage;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("InitPatched Exception ", e);
            }
        }
        
        private static void LargeTurretBaseInitPatched(MyLargeTurretBaseDefinition __instance)
        {
            try
            {
                __instance.GeneralDamageMultiplier = __instance.GeneralDamageMultiplier * SentisOptimisationsPlugin.Config.TurretsDamageMultiplier;
                __instance.InventoryMaxVolume = __instance.InventoryMaxVolume * 10;
                __instance.InventoryFillFactorMin = 0.8f;
                __instance.InventoryFillFactorMin = 1;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("InitPatched Exception ", e);
            }
        }
        
        private static void WeaponDefinitionInitPatched(MyWeaponBlockDefinition __instance)
        {
            try
            {
                if (__instance.Id.SubtypeName.Contains("LargeMissileLauncher"))
                {
                    __instance.GeneralDamageMultiplier = __instance.GeneralDamageMultiplier * (SentisOptimisationsPlugin.Config.TurretsDamageMultiplier / 2);
                    __instance.InventoryMaxVolume = __instance.InventoryMaxVolume * 10;
                    __instance.InventoryFillFactorMin = 0.8f;
                    __instance.InventoryFillFactorMin = 1;
                }
                
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn("WeaponDefinitionInitPatched Exception ", e);
            }
        }
        //
        // private static void ProjectileAmmoDefinitionInitPatched(MyProjectileAmmoDefinition __instance)
        // {
        //     try
        //     {
        //         __instance.ProjectileMassDamage = 500;
        //     }
        //     catch (Exception e)
        //     {
        //         SentisOptimisationsPlugin.Log.Warn("ProjectileAmmoDefinitionInitPatched Exception ", e);
        //     }
        // }

    }
}