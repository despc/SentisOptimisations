using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisOptimisationsPlugin;
using SentisOptimisationsPlugin.Freezer;
using SpaceEngineers.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Only idle acquisition is reduced to 20 frames, with a phase for each block, not each grid.
    /// A non-null Target (including a potential target) uses a 10-frame search phase for controllers.
    /// Controllers keep their per-frame aiming/shooting; vanilla turrets keep their complete Frame10 timer.
    /// Vanilla idle searches are driven by the plugin's per-frame update: a modulo gate on Frame10 alone
    /// permanently starves blocks whose phase is not aligned to that timer.
    /// </summary>
    [PatchShim]
    public static class TurretControlTargetSelect
    {
        /// <summary>Idle acquisition period for both kinds of turret.</summary>
        public const int Period = 20;
        public const int TargetPeriod = 10;

        private static readonly MethodInfo Search = typeof(MyLargeTurretTargetingSystem).GetMethod(nameof(MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel),
            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

        private static readonly TurretSearchScheduler<MyLargeTurretBase> Vanilla = new TurretSearchScheduler<MyLargeTurretBase>();
        private static readonly Action<MyLargeTurretBase> SearchIdle = SearchIdleVanilla;
        private static MySession _session;
        private static readonly Func<MyLargeTurretBase, bool> WorkingFlags = Accessors.Field<MyLargeTurretBase, bool>("m_workingFlagCombination");
        private static readonly Func<MyLargeTurretBase, bool> Creative = Accessors.Method<MyLargeTurretBase, Func<MyLargeTurretBase, bool>>("get_IsCreativeModeEnabled");
        private static readonly Func<MyLargeTurretBase, bool> HasAmmo = Accessors.Method<MyLargeTurretBase, Func<MyLargeTurretBase, bool>>("HasEnoughAmmo");

        /// <summary>Controller search calls allowed/skipped; compatible with reg_rail_turret_ship reflection.</summary>
        public static long Started, Skipped;
        public static long VanillaStarted, VanillaDeferred;

        /// <summary>Optional live-regression probe: block id, simulation frame, target present, vanilla turret.</summary>
        public static Action<long, ulong, bool, bool> SearchObserved;

        internal static void Observe(long blockId, ulong frame, bool hasTarget, bool normalTurret)
        {
            // A diagnostic consumer must never interfere with aiming or firing.
            try { SearchObserved?.Invoke(blockId, frame, hasTarget, normalTurret); }
            catch { }
        }

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("TurretControlTargetSelect", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (Search == null) throw new MissingMethodException("MyLargeTurretTargetingSystem.CheckAndSelectNearTargetsParallel");
            var update = typeof(MyTurretControlBlock).GetMethod(nameof(MyTurretControlBlock.UpdateAfterSimulation), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (update == null || update.DeclaringType != typeof(MyTurretControlBlock))
                throw new MissingMethodException("MyTurretControlBlock.UpdateAfterSimulation");
            var timer = typeof(MyLargeTurretBase).GetMethod(nameof(MyLargeTurretBase.DoUpdateTimerTick), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (timer == null || timer.DeclaringType != typeof(MyLargeTurretBase))
                throw new MissingMethodException("MyLargeTurretBase.DoUpdateTimerTick");
            ctx.GetPattern(update).Transpilers.Add(typeof(TurretControlTargetSelect).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(timer).Transpilers.Add(typeof(TurretControlTargetSelect).GetMethod(nameof(VanillaTranspiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        internal static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions) => ReplaceSearch(instructions, nameof(SearchDue));
        internal static IEnumerable<MsilInstruction> VanillaTranspiler(IEnumerable<MsilInstruction> instructions) => ReplaceSearch(instructions, nameof(SearchVanillaDue));

        private static IEnumerable<MsilInstruction> ReplaceSearch(IEnumerable<MsilInstruction> instructions, string wrapper)
        {
            var list = instructions.ToList();
            var replaced = 0;
            var throttled = typeof(TurretControlTargetSelect).GetMethod(wrapper, BindingFlags.Static | BindingFlags.Public);
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].OpCode != OpCodes.Call && list[i].OpCode != OpCodes.Callvirt) continue;
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand) || operand.Value != Search) continue;
                var self = new MsilInstruction(OpCodes.Ldarg_0);
                // a branch to the call goes to the block's load before it now
                foreach (var label in list[i].Labels) self.Labels.Add(label);
                list[i] = new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)throttled);
                list.Insert(i, self);
                i++;
                replaced++;
            }
            if (replaced != 2)
                throw new InvalidOperationException("TurretControlTargetSelect: expected 2 target searches for wrapper " + wrapper + ", found " + replaced);
            return list;
        }

        /// <summary>Per-block search phase: 20 frames idle, 10 with a target. Aiming is not gated.</summary>
        public static void SearchDue(MyLargeTurretTargetingSystem system, MyTurretControlBlock block)
        {
            if (Due(block))
            {
                Started++;
                Observe(block?.EntityId ?? 0, MySandboxGame.Static?.SimulationFrameCounter ?? 0, system.Target != null, false);
                system.CheckAndSelectNearTargetsParallel();
            }
            else Skipped++;
        }

        public static void SearchVanillaDue(MyLargeTurretTargetingSystem system, MyLargeTurretBase block)
        {
            if (system.Target != null || block == null || MySandboxGame.Static == null || MySession.Static == null)
            {
                VanillaStarted++;
                Observe(block?.EntityId ?? 0, MySandboxGame.Static?.SimulationFrameCounter ?? 0, system.Target != null, true);
                system.CheckAndSelectNearTargetsParallel();
                return;
            }
            EnsureSession();
            Vanilla.Register(block, block.EntityId);
            VanillaDeferred++;
        }

        /// <summary>Game-thread update, even when idle turrets disable EACH_FRAME_AFTER.</summary>
        public static void Tick()
        {
            EnsureSession();
            if (_session != null && Sync.IsServer && MySandboxGame.Static != null)
                Vanilla.Tick(MySandboxGame.Static.SimulationFrameCounter, SearchIdle);
        }

        private static void EnsureSession()
        {
            if (ReferenceEquals(_session, MySession.Static)) return;
            Vanilla.Clear();
            SearchObserved = null;
            _session = MySession.Static;
        }

        private static void SearchIdleVanilla(MyLargeTurretBase block)
        {
            // Re-read all vanilla search preconditions at execution time, not their last timer values.
            // FocusTargetGrid remains in the original timer, before its wrapped search calls.
            if (block.Closed || block.MarkedForClose || block.CubeGrid == null ||
                FreezeLogic.FrozenGrids.Contains(block.CubeGrid.EntityId) ||
                !block.Render.IsVisible() || !block.IsWorking || !block.Enabled ||
                !((Sandbox.ModAPI.IMyFunctionalBlock)block).IsUpdateTimerEnabled) return;
            var system = block.TargetingSystem;
            if (system.Target != null || block.TurretController.IsControlled || !Sync.IsServer ||
                system.TargetPrediction.IsTargetPositionManual || !WorkingFlags(block) ||
                (!Creative(block) && !HasAmmo(block))) return;
            VanillaStarted++;
            Observe(block.EntityId, MySandboxGame.Static.SimulationFrameCounter, false, true);
            system.CheckAndSelectNearTargetsParallel();
        }

        internal static bool Due(MyTurretControlBlock block)
        {
            if (block == null || MySandboxGame.Static == null) return true;
            return Due(MySandboxGame.Static.SimulationFrameCounter, block.EntityId, block.Target != null);
        }

        internal static bool Due(ulong frame, long blockId, bool hasTarget)
        {
            var period = (ulong)(hasTarget ? TargetPeriod : Period);
            return frame % period == (ulong)blockId % period;
        }
    }
}
