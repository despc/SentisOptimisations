using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.WorldEnvironment;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A planet's environment sector (trees, bushes) that cannot finish its switch does not take the server down.
    ///
    /// <c>MyEnvironmentSector.DoSerialWork</c> commits on the game thread what the parallel work prepared: the modules'
    /// lod and physics, the render, the physics body. A NullReferenceException in it, right after the last player near a
    /// sector left (02.10.2026, the sectors around him switching off), went up through
    /// <c>MyPlanetEnvironmentComponent.SerialWorkCallback</c> and the session update: the server gone. What was null the
    /// stack does not tell; the likely one is <c>Physics</c> - the sector turning its physics off with no physics body,
    /// which the game's own <c>EnablePhysics</c> checks for and DoSerialWork does not.
    ///
    /// Here the exception stops at DoSerialWork: the callback goes on with the other sectors, and the sector is left as
    /// the end of the method leaves it - no serial work pending, and with no body its physics counted off. The log says
    /// what was null.
    /// </summary>
    [PatchShim]
    public static class EnvironmentSectorSerialWork
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly PropertyInfo HasPhysicsProperty = typeof(MyEnvironmentSector).GetProperty(nameof(MyEnvironmentSector.HasPhysics), Any);
        private static readonly PropertyInfo SerialPendingProperty = typeof(MyEnvironmentSector).GetProperty(nameof(MyEnvironmentSector.HasSerialWorkPending), Any);
        private static readonly FieldInfo TogglePhysicsField = typeof(MyEnvironmentSector).GetField("m_togglePhysics", Any);
        private static readonly FieldInfo ModulesField = typeof(MyEnvironmentSector).GetField("m_modules", Any);
        private static readonly FieldInfo RenderField = typeof(MyEnvironmentSector).GetField("m_render", Any);

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EnvironmentSectorSerialWork", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var method = typeof(MyEnvironmentSector).GetMethod(nameof(MyEnvironmentSector.DoSerialWork), Any, null, Type.EmptyTypes, null)
                         ?? throw new MissingMethodException(nameof(MyEnvironmentSector), nameof(MyEnvironmentSector.DoSerialWork));
            if (HasPhysicsProperty?.GetSetMethod(true) == null || SerialPendingProperty?.GetSetMethod(true) == null || TogglePhysicsField == null)
                throw new MissingMemberException(nameof(MyEnvironmentSector), "HasPhysics / HasSerialWorkPending / m_togglePhysics");
            CrashFixPatch.harmony.Patch(method, finalizer: new HarmonyMethod(typeof(EnvironmentSectorSerialWork).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static Exception Finalizer(Exception __exception, MyEnvironmentSector __instance, ref bool __result)
        {
            if (__exception == null) return null;
            __result = false;
            try
            {
                var physics = __instance.Physics;
                var hasPhysics = __instance.HasPhysics;
                var toggle = (bool)TogglePhysicsField.GetValue(__instance);
                var nullProxies = 0;
                var nullModules = 0;
                if (ModulesField?.GetValue(__instance) is IDictionary modules)
                    foreach (var module in modules.Values.Cast<object>())
                    {
                        if (module == null) { nullModules++; continue; }
                        if (module.GetType().GetField("Proxy", Any)?.GetValue(module) == null) nullProxies++;
                    }
                SentisOptimisationsPlugin.Log.Error(__exception, "Environment sector " + __instance.EntityId + " '" + __instance.DisplayName + "' could not finish its switch: " +
                                                                 "physics body " + (physics == null ? "null" : "enabled " + physics.Enabled) + ", has physics " + hasPhysics +
                                                                 ", toggling physics " + toggle + ", modules null " + nullModules + ", proxies null " + nullProxies +
                                                                 ", render " + (RenderField?.GetValue(__instance) == null ? "null" : "set") +
                                                                 ", closed " + __instance.Closed + ", marked " + __instance.MarkedForClose + " - left as it is, the server goes on");

                // as the end of the method leaves it
                if (toggle && hasPhysics && physics == null)
                {
                    HasPhysicsProperty.SetValue(__instance, false);
                    TogglePhysicsField.SetValue(__instance, false);
                }
                SerialPendingProperty.SetValue(__instance, false);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(__exception, "Environment sector DoSerialWork (" + e.Message + ")");
            }
            return null;
        }
    }
}
