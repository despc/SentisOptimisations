using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// Scripts run three times less often while the server load is over 70% (Slowdowns: "Scripts three times less often
    /// at load over 70%").
    ///
    /// The load is the one the clients are shown (<c>MySandboxGame.CPULoad</c>: the game thread's time of a frame
    /// against 16.67 ms), averaged over a second (<see cref="PeakLoad.Busy"/>). The update types a programmable block's
    /// script gets in a frame are gathered before <c>MyIngameScriptComponent.UpdateAfterSimulation</c> runs it; here
    /// they are thinned out:
    /// <list type="bullet">
    /// <item><c>Update1</c> (every frame) is given on one frame in 3, and only to a block with no player within
    /// <see cref="PlayerRadiusM"/>: a script someone is near (a ship's flight, a door, a display) runs as ever;</item>
    /// <item><c>Update10</c> is given on one of its runs in 3 (every 30 frames), <c>Update100</c> on one in 3 (every
    /// 300 frames), players near or not;</item>
    /// <item><c>Once</c> always.</item>
    /// </list>
    /// Each block has its own frames (its phase from its entity id), so the scripts of a base do not all run in the
    /// same frame. A frame left with nothing to give is skipped, the script not called.
    /// </summary>
    [PatchShim]
    public static class ScriptSlowdown
    {
        public const int Period = 3;

        /// <summary>A block with a player this near, m, keeps its Update1.</summary>
        public const double PlayerRadiusM = 5000;

        /// <summary>Script runs not made (whole frames skipped).</summary>
        public static long Skipped;

        /// <summary>Whether a player is within <see cref="PlayerRadiusM"/> of a point (replaceable for checks).</summary>
        public static Func<Vector3D, bool> PlayerNear = PlayersNear;

        private static Func<MyIngameScriptComponent, UpdateType> _updateType;
        private static Action<MyIngameScriptComponent, UpdateType> _setUpdateType;
        private static Func<MyIngameScriptComponent, Sandbox.Game.Entities.Blocks.MyProgrammableBlock> _block;

        public static void Patch(PatchContext ctx) => PatchGuard.Run("ScriptSlowdown", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyIngameScriptComponent);
            var updateType = type.GetField("m_updateType", instance) ?? throw new MissingFieldException(type.Name, "m_updateType");
            var block = type.GetField("m_block", instance) ?? throw new MissingFieldException(type.Name, "m_block");
            var update = type.GetMethod("UpdateAfterSimulation", instance, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.Name, "UpdateAfterSimulation");
            var component = Expression.Parameter(type);
            var value = Expression.Parameter(typeof(UpdateType));
            _updateType = Expression.Lambda<Func<MyIngameScriptComponent, UpdateType>>(Expression.Field(component, updateType), component).Compile();
            _setUpdateType = Expression.Lambda<Action<MyIngameScriptComponent, UpdateType>>(Expression.Assign(Expression.Field(component, updateType), value), component, value).Compile();
            _block = Expression.Lambda<Func<MyIngameScriptComponent, Sandbox.Game.Entities.Blocks.MyProgrammableBlock>>(Expression.Field(component, block), component).Compile();
            ctx.GetPattern(update).Prefixes.Add(typeof(ScriptSlowdown).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>
        /// The update types a script gets in this frame while the server is busy: Update1 on its turn of 3 frames when no
        /// player is near, Update10 and Update100 on their turn of 3 runs, Once always.
        /// </summary>
        public static UpdateType Thin(UpdateType types, long blockId, ulong frame, bool playerNear)
        {
            var left = types;
            if ((types & UpdateType.Update1) != 0 && !playerNear && !PeakLoad.Turn(blockId, frame, Period))
                left &= ~UpdateType.Update1;
            // a block's Update10 comes on frames 10 apart (Update100 - 100 apart): its runs are counted by frame / 10
            if ((types & UpdateType.Update10) != 0 && !PeakLoad.Turn(blockId, frame / 10, Period))
                left &= ~UpdateType.Update10;
            if ((types & UpdateType.Update100) != 0 && !PeakLoad.Turn(blockId, frame / 100, Period))
                left &= ~UpdateType.Update100;
            return left;
        }

        private static bool Prefix(MyIngameScriptComponent __instance)
        {
            if (!PeakLoad.Busy) return true;
            var config = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
            if (config == null || !config.SlowScripts) return true;
            try
            {
                var session = Sandbox.Game.World.MySession.Static;
                var block = _block(__instance);
                var types = _updateType(__instance);
                if (session == null || block == null || types == UpdateType.None) return true;
                var frame = (ulong)session.GameplayFrameCounter;
                // the players are looked up only for a script that has Update1 to lose
                var near = (types & UpdateType.Update1) != 0 && PlayerNear(block.PositionComp.GetPosition());
                var left = Thin(types, block.EntityId, frame, near);
                if (left == types) return true;
                if (left == UpdateType.None)
                {
                    // the whole update is the script's run (the base one does nothing)
                    Interlocked.Increment(ref Skipped);
                    return false;
                }
                _setUpdateType(__instance, left);
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // the players' positions, taken once a second (scripts run on the game thread)
        private static readonly List<Vector3D> Players = new List<Vector3D>();
        private static ulong _playersFrame = ulong.MaxValue;

        private static bool PlayersNear(Vector3D point)
        {
            var session = Sandbox.Game.World.MySession.Static;
            if (session?.Players == null) return true;
            var frame = (ulong)session.GameplayFrameCounter;
            if (_playersFrame == ulong.MaxValue || frame - _playersFrame >= 60 || frame < _playersFrame)
            {
                _playersFrame = frame;
                Players.Clear();
                foreach (var player in session.Players.GetOnlinePlayers())
                {
                    var entity = player.Controller?.ControlledEntity?.Entity ?? player.Character;
                    if (entity?.PositionComp != null) Players.Add(entity.PositionComp.GetPosition());
                }
            }
            const double r2 = PlayerRadiusM * PlayerRadiusM;
            foreach (var p in Players)
                if (Vector3D.DistanceSquared(p, point) <= r2) return true;
            return false;
        }
    }
}
