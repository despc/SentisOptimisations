using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// What a ship drill does every frame, done only when something needs it.
    ///
    /// <b>The every-frame update runs every tenth frame on the dedicated server.</b>
    /// MyShipDrill.UpdateAfterSimulation runs every frame for every switched-on drill. On a
    /// dedicated server it only keeps the drill-head animation speed (time based, so it does not
    /// care how often it runs), the dust effect and head rotation (render only), the camera shake
    /// of a local player (none), the tool shake force (only with the world's EnableToolShake, off
    /// by default) and the pilot's mining-time statistic - and to decide that it checks access,
    /// safe zones and power (CanShoot) every frame. With 1152 drills that was ~1 ms of every frame.
    /// Drills of a ship somebody is piloting and worlds with tool shake keep the vanilla update, so
    /// the shake force and the mining-time statistic are unchanged. Drills are spread over the ten
    /// frames by entity id.
    ///
    /// <b>The drill's sensor and cut-out sphere follow the ship lazily.</b> Every time a ship moves,
    /// every drill on it recomputes its world matrix and moves its sensor and cut-out sphere
    /// (MyDrillBase.UpdatePosition) - for a moving mining ship that is every frame, ~0.4 ms a frame
    /// for 1152 drills. They are read only when the drill drills (every 90 frames), checks whether
    /// it may (CanShoot), hands out ore, runs its every-frame update, or when anyone takes its
    /// DrillBase; a moved drill is now only marked, and the position is brought up to date right
    /// before each of those. Moves reported off the game thread update at once, as in vanilla.
    /// </summary>
    [PatchShim]
    public static class DrillFrameUpdate
    {
        public const int PeriodFrames = 10;

        private static readonly FieldInfo DrillBaseField =
            typeof(MyShipDrill).GetField("m_drillBase", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<MyShipDrill, MyDrillBase> GetDrillBase = BuildGetter();
        private static readonly HashSet<MyShipDrill> Moved = new HashSet<MyShipDrill>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("DrillFrameUpdate", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(DrillFrameUpdate);
            var drill = typeof(MyShipDrill);
            ctx.GetPattern(drill.GetMethod(nameof(MyShipDrill.UpdateAfterSimulation), instance, null, Type.EmptyTypes, null))
                .Prefixes.Add(self.GetMethod(nameof(UpdateAfterSimulationPrefix), statics));

            if (GetDrillBase == null) return;
            ctx.GetPattern(drill.GetMethod("WorldPositionChanged", instance))
                .Transpilers.Add(self.GetMethod(nameof(WorldPositionChangedTranspiler), statics));
            var ensure = self.GetMethod(nameof(EnsurePositionPrefix), statics);
            ctx.GetPattern(drill.GetMethod(nameof(MyShipDrill.UpdateBeforeSimulation10), instance, null, Type.EmptyTypes, null)).Prefixes.Add(ensure);
            ctx.GetPattern(drill.GetMethod(nameof(MyShipDrill.CanShoot), instance)).Prefixes.Add(ensure);
            ctx.GetPattern(drill.GetMethod(nameof(MyShipDrill.OnDrillResults), instance)).Prefixes.Add(ensure);
            ctx.GetPattern(drill.GetProperty(nameof(MyShipDrill.DrillBase), instance).GetMethod).Prefixes.Add(ensure);
            ctx.GetPattern(drill.GetMethod("Closing", instance)).Prefixes.Add(self.GetMethod(nameof(ClosingPrefix), statics));
        }

        private static Func<MyShipDrill, MyDrillBase> BuildGetter()
        {
            if (DrillBaseField == null) return null;
            var method = new DynamicMethod("GetDrillBase", typeof(MyDrillBase), new[] { typeof(MyShipDrill) }, typeof(MyShipDrill), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, DrillBaseField);
            il.Emit(OpCodes.Ret);
            return (Func<MyShipDrill, MyDrillBase>)method.CreateDelegate(typeof(Func<MyShipDrill, MyDrillBase>));
        }

        private static bool UpdateAfterSimulationPrefix(MyShipDrill __instance)
        {
            if (Sync.IsDedicated && MySession.Static != null && !MySession.Static.EnableToolShake &&
                (MySandboxGame.Static.SimulationFrameCounter + (ulong)__instance.EntityId) % PeriodFrames != 0)
            {
                var grid = __instance.CubeGrid;
                if (grid == null || Sync.Players.GetControllingPlayer(grid) == null) return false;
            }
            EnsurePosition(__instance);
            return true;
        }

        /// <summary>
        /// Replaces <c>m_drillBase.UpdatePosition(base.WorldMatrix)</c> with <c>MarkMoved(this)</c>.
        /// </summary>
        private static IEnumerable<MsilInstruction> WorldPositionChangedTranspiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var update = typeof(MyDrillBase).GetMethod(nameof(MyDrillBase.UpdatePosition));
            var at = list.FindIndex(i => i.OpCode == OpCodes.Callvirt && i.Operand is MsilOperandInline<MethodBase> m && m.Value == update);
            // ldarg.0; ldfld m_drillBase; ldarg.0; call get_WorldMatrix; callvirt UpdatePosition
            if (at < 4 || list[at - 4].OpCode != OpCodes.Ldarg_0 || list[at - 3].OpCode != OpCodes.Ldfld ||
                list[at - 2].OpCode != OpCodes.Ldarg_0 || list[at - 1].OpCode != OpCodes.Call ||
                list.Skip(at - 3).Take(4).Any(i => i.Labels.Count > 0))
                throw new InvalidOperationException("DrillFrameUpdate: unexpected MyShipDrill.WorldPositionChanged IL");
            var mark = new MsilInstruction(OpCodes.Call).InlineValue(
                (MethodBase)typeof(DrillFrameUpdate).GetMethod(nameof(MarkMoved), BindingFlags.Static | BindingFlags.Public));
            list.RemoveRange(at - 3, 4);
            list.Insert(at - 3, mark);
            return list;
        }

        public static void MarkMoved(MyShipDrill drill)
        {
            if (MySandboxGame.Static?.UpdateThread == Thread.CurrentThread)
                Moved.Add(drill);
            else
                GetDrillBase(drill)?.UpdatePosition(drill.WorldMatrix);
        }

        private static void EnsurePositionPrefix(MyShipDrill __instance) => EnsurePosition(__instance);

        private static void EnsurePosition(MyShipDrill drill)
        {
            if (Moved.Count == 0 || MySandboxGame.Static?.UpdateThread != Thread.CurrentThread || !Moved.Remove(drill)) return;
            GetDrillBase(drill)?.UpdatePosition(drill.WorldMatrix);
        }

        private static void ClosingPrefix(MyShipDrill __instance)
        {
            if (Moved.Count > 0 && MySandboxGame.Static?.UpdateThread == Thread.CurrentThread) Moved.Remove(__instance);
        }
    }
}
