using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace SentisOptimisations.Slowdowns
{
    /// <summary>
    /// Multigrid Projector's projections follow their projectors less often.
    ///
    /// Multigrid Projector moves a projector's projected grids to where the projector is every frame
    /// (<c>MultigridProjection.UpdateGridTransformations</c>, in its <c>UpdateAfterSimulation</c>): a projector on a
    /// grid that moves at all (a ship, drifting or turning ever so little) sets the world matrix of every projected grid,
    /// and with it of every projected block, every frame - 2.4 s of a minute's trace of the live server at its peak
    /// (11.11.2026, 2.1 s of it in <c>SetWorldMatrix</c>), the most of it on the game thread inside the heaviest
    /// cluster. On the server a projection's place matters only to what welds it: the clients place their own.
    /// <list type="bullet">
    /// <item>Always: a projection with no enabled ship welder within its reach (twice its sensor radius and 5 m) and no
    /// player holding a welder within <see cref="HandReachM"/> of the blocks it could build now (Multigrid Projector's
    /// published "buildable" blocks; none - nothing to weld) is moved once in
    /// <see cref="IdleFrames"/> frames (a second). Looked up on the game thread once a second for each projection, the
    /// projections spread over the frames; a welder come near is seen within a second, and the projection is moved at
    /// once then. A projection whose offset or rotation is changed in the terminal is put there at once.</item>
    /// <item>With a welder near, every frame, at the peak too: a welder on a ship at the top speed found nothing to build
    /// while the projection followed once in 4 updates, 5 m behind (reg_projection_flying_weld_slowdowns, 12.11.2026:
    /// 0 of 2 blocks in 60 s) - the peak's slowdown of the projections is gone for that.</item>
    /// </list>
    /// The rest of the projection's update (what can be built, the welding) runs every frame as ever. Patched (Harmony:
    /// no one else patches this method of the other plugin) once the session is up, when Multigrid Projector is surely
    /// loaded; without it nothing is done.
    /// </summary>
    public static class ProjectionSlowdown
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>A projection with no welder near is moved once in this many frames.</summary>
        public const int IdleFrames = 60;

        /// <summary>How far from the box of a projection's buildable blocks entities are looked up, m.</summary>
        public const double WelderMarginM = 25;

        /// <summary>A player holding a welder this near the box of the buildable blocks counts as near, m.</summary>
        public const double HandReachM = 10;

        /// <summary>Moves skipped with no welder near.</summary>
        public static long IdleSkipped;

        /// <summary>For checks of Multigrid Projector itself: the projections of these projectors (entity ids) moved every frame, as without this.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> IdleOffFor = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();

        /// <summary>The projections known, and how many of them have a welder near, at the last look.</summary>
        public static int Known, WithWelders;

        private static Harmony _harmony;
        private static bool _tried;
        private static Func<object, MyProjectorBase> _projector;
        private static Func<object, List<MyCubeGrid>> _previewGrids;

        /// <summary>From the plugin's update: patches once, after the session is loaded.</summary>
        public static void EnsurePatched()
        {
            if (_tried || MySession.Static == null || !MySession.Static.Ready) return;
            _tried = true;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MultigridProjector.Logic.MultigridProjection", false)).FirstOrDefault(t => t != null);
                if (type == null)
                {
                    Log.Info("Projection slowdown: Multigrid Projector is not loaded, nothing to slow down");
                    return;
                }
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var method = type.GetMethod("UpdateGridTransformations", instance, null, Type.EmptyTypes, null)
                             ?? throw new MissingMethodException(type.Name, "UpdateGridTransformations");
                var projector = (MemberInfo)type.GetField("Projector", instance) ?? type.GetProperty("Projector", instance)
                                ?? throw new MissingMemberException(type.Name, "Projector");
                var previews = (MemberInfo)type.GetProperty("PreviewGrids", instance) ?? type.GetField("PreviewGrids", instance)
                               ?? throw new MissingMemberException(type.Name, "PreviewGrids");
                var self = System.Linq.Expressions.Expression.Parameter(typeof(object));
                var typed = System.Linq.Expressions.Expression.Convert(self, type);
                _projector = System.Linq.Expressions.Expression.Lambda<Func<object, MyProjectorBase>>(
                    System.Linq.Expressions.Expression.TypeAs(System.Linq.Expressions.Expression.MakeMemberAccess(typed, projector), typeof(MyProjectorBase)), self).Compile();
                _previewGrids = System.Linq.Expressions.Expression.Lambda<Func<object, List<MyCubeGrid>>>(
                    System.Linq.Expressions.Expression.TypeAs(System.Linq.Expressions.Expression.MakeMemberAccess(typed, previews), typeof(List<MyCubeGrid>)), self).Compile();
                BindBuildable(type);
                _harmony = new Harmony("SentisOptimisations.ProjectionSlowdown");
                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProjectionSlowdown).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.Info("Projection slowdown: Multigrid Projector's projections patched");
            }
            catch (Exception e)
            {
                Log.Error(e, "Projection slowdown: Multigrid Projector is not what this knows, not slowed down");
            }
        }

        private sealed class State
        {
            public object Projection;
            public MyProjectorBase Projector;
            // until looked at: as if a welder were near (moved as ever)
            public volatile bool WeldersNear = true;
            public long LastMoveFrame = long.MinValue / 2;
            // where the projection was put relative to its projector when last moved (the terminal's offset and rotation)
            public Vector3I Offset, Rotation;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, State> States = new System.Runtime.CompilerServices.ConditionalWeakTable<object, State>();
        private static readonly List<State> All = new List<State>();
        private static int _next;

        /// <summary>Whether a projection with no welder near, last moved on that frame, is not moved on this one.</summary>
        public static bool IdleSkips(long frame, long lastMoveFrame) => frame - lastMoveFrame < IdleFrames;

        private static State StateOf(object projection)
        {
            if (States.TryGetValue(projection, out var state)) return state;
            lock (All)
            {
                if (States.TryGetValue(projection, out state)) return state;
                state = new State { Projection = projection, Projector = _projector(projection) };
                States.Add(projection, state);
                All.Add(state);
                return state;
            }
        }

        // in the projection's update: on the threads of the clusters
        private static bool Prefix(object __instance)
        {
            try
            {
                var session = MySession.Static;
                if (session == null) return true;
                var state = StateOf(__instance);
                var frame = (long)session.GameplayFrameCounter;
                var projector = state.Projector;
                // the offset or the rotation changed in the terminal: put where asked at once
                var moved = projector != null && (projector.ProjectionOffset != state.Offset || projector.ProjectionRotation != state.Rotation);
                if (moved)
                {
                    state.Offset = projector.ProjectionOffset;
                    state.Rotation = projector.ProjectionRotation;
                }
                if (!state.WeldersNear && (IdleOffFor.IsEmpty || projector == null || !IdleOffFor.ContainsKey(projector.EntityId)))
                {
                    if (!moved && IdleSkips(frame, state.LastMoveFrame))
                    {
                        Interlocked.Increment(ref IdleSkipped);
                        return false;
                    }
                    state.LastMoveFrame = frame;
                    return true;
                }
                state.LastMoveFrame = frame;
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static readonly List<MyEntity> Found = new List<MyEntity>();
        private static readonly VRage.ObjectBuilders.MyObjectBuilderType WelderType = typeof(MyObjectBuilder_ShipWelder);

        /// <summary>Every frame on the game thread: a part of the projections looked at, each once a second.</summary>
        public static void Refresh()
        {
            if (_previewGrids == null || _buildable == null) return;
            State[] some;
            lock (All)
            {
                All.RemoveAll(s => s.Projector == null || s.Projector.Closed || s.Projector.MarkedForClose);
                Known = All.Count;
                var near = 0;
                foreach (var s in All) if (s.WeldersNear) near++;
                WithWelders = near;
                if (All.Count == 0) return;
                var count = (All.Count + IdleFrames - 1) / IdleFrames;
                some = new State[count];
                for (var i = 0; i < count; i++)
                {
                    if (_next >= All.Count) _next = 0;
                    some[i] = All[_next++];
                }
            }
            foreach (var state in some) state.WeldersNear = WeldersNear(state);
        }

        private static bool WeldersNear(State state)
        {
            try
            {
                var projector = state.Projector;
                if (projector == null || _buildable == null) return true;
                // the blocks a welder could build now: their box in the world
                var box = BoundingBoxD.CreateInvalid();
                var any = false;
                foreach (var subgrid in _subgrids(state.Projection))
                {
                    var preview = _previewGrid(subgrid);
                    if (preview?.PositionComp == null) continue;
                    Cells.Clear();
                    _buildable(subgrid, Cells);
                    var half = new Vector3D(preview.GridSize / 2);
                    foreach (var cell in Cells)
                    {
                        var at = preview.GridIntegerToWorld(cell);
                        box.Include(new BoundingBoxD(at - half, at + half));
                        any = true;
                    }
                }
                Cells.Clear();
                // nothing to build now: nothing a welder could do with the projection's place
                if (!any) return false;
                var reach = box.GetInflated(WelderMarginM);
                Found.Clear();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref reach, Found);
                foreach (var entity in Found)
                {
                    if (entity is MyCharacter character)
                    {
                        if (character.EquippedTool is MyWelder && box.Distance(character.PositionComp.GetPosition()) <= HandReachM) return true;
                        continue;
                    }
                    if (!(entity is MyCubeGrid grid) || grid.IsPreview || grid.Closed) continue;
                    if (!grid.BlocksCounters.TryGetValue(WelderType, out var welders) || welders <= 0) continue;
                    foreach (var welder in grid.GetFatBlocks<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>())
                    {
                        if (!welder.Enabled || !welder.IsFunctional) continue;
                        // its sensor sphere sits in front of it: within its diameter and a block of the box
                        var welderReach = 2 * SentisOptimisationsPlugin.ShipTool.ShipToolPatch.GetWelderRadius(welder) + 5;
                        if (box.Distance(welder.PositionComp.GetPosition()) <= welderReach) return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                // not sure: moved as ever
                return true;
            }
            finally
            {
                Found.Clear();
            }
        }

        private static readonly List<Vector3I> Cells = new List<Vector3I>();

        // the projection's buildable cells, through Multigrid Projector's published states (its background scan's)
        private static Func<object, System.Collections.IEnumerable> _subgrids;
        private static Func<object, MyCubeGrid> _previewGrid;
        private static Action<object, List<Vector3I>> _buildable;

        private static void Collect<T>(IEnumerable<ValueTuple<Vector3I, T>> states, List<Vector3I> into)
        {
            foreach (var state in states) into.Add(state.Item1);
        }

        private static void BindBuildable(Type projection)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var get = projection.GetMethod("GetSupportedSubgrids", instance, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(projection.Name, "GetSupportedSubgrids");
            var subgridType = get.ReturnType.GetElementType() ?? throw new MissingMemberException(projection.Name, "GetSupportedSubgrids[]");
            var iter = subgridType.GetMethod("IterPublishedBlockStates", instance) ?? throw new MissingMethodException(subgridType.Name, "IterPublishedBlockStates");
            var previewGrid = (MemberInfo)subgridType.GetField("PreviewGrid", instance) ?? subgridType.GetProperty("PreviewGrid", instance)
                              ?? throw new MissingMemberException(subgridType.Name, "PreviewGrid");
            var stateType = iter.ReturnType.GetGenericArguments()[0].GetGenericArguments()[1];
            var buildable = (int)Enum.Parse(stateType, "Buildable");
            var p = System.Linq.Expressions.Expression.Parameter(typeof(object));
            var list = System.Linq.Expressions.Expression.Parameter(typeof(List<Vector3I>));
            _subgrids = System.Linq.Expressions.Expression.Lambda<Func<object, System.Collections.IEnumerable>>(
                System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(p, projection), get), p).Compile();
            var sub = System.Linq.Expressions.Expression.Convert(p, subgridType);
            _previewGrid = System.Linq.Expressions.Expression.Lambda<Func<object, MyCubeGrid>>(
                System.Linq.Expressions.Expression.TypeAs(System.Linq.Expressions.Expression.MakeMemberAccess(sub, previewGrid), typeof(MyCubeGrid)), p).Compile();
            var all = System.Linq.Expressions.Expression.Constant(new BoundingBoxI(Vector3I.MinValue, Vector3I.MaxValue));
            var states = System.Linq.Expressions.Expression.Call(sub, iter, all, System.Linq.Expressions.Expression.Constant(buildable));
            var collect = typeof(ProjectionSlowdown).GetMethod(nameof(Collect), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(stateType);
            _buildable = System.Linq.Expressions.Expression.Lambda<Action<object, List<Vector3I>>>(
                System.Linq.Expressions.Expression.Call(collect, states, list), p, list).Compile();
        }
    }
}
