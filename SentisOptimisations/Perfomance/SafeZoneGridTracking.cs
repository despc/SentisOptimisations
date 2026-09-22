using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using Sandbox.Common.ObjectBuilders;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using SentisOptimisationsPlugin;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Safe zones find the dynamic grids inside them by geometry, not through their physics phantom.
    ///
    /// A zone learns what is inside it from a phantom body: Havok tells it when a body starts and stops
    /// touching it. A dynamic grid that changes its shape - every block welded, ground or passing a build
    /// stage - is a new shape to Havok, which drops and rebuilds its contacts with the zone, one per part of
    /// the shape, and tells the zone that the grid left and came back. A 2757-block ship welded by
    /// projection inside a zone gave 600-900 such pairs every frame and made the frame 2.7 ms dearer, almost
    /// all of it inside Havok, where nothing on the managed side can reach it.
    ///
    /// So the zone's phantom goes on collision layer 1, which no other body uses and which is set up like
    /// the dynamic grids' layer 15 except that it does not collide with any grid layer: the phantom still
    /// sees characters, floating objects, missiles and meteors, but no grid. Static grids never reached it
    /// anyway - the game inserts them by geometry when they appear or change - and the dynamic ones are
    /// looked for here, each zone once every <see cref="Period"/> frames: a grid wholly inside is inserted,
    /// one across the border is inserted if its shape reaches into the zone, and a grid that is no longer
    /// near is removed. The game's own insert and remove do the rest - the motor locks, the pilots, the
    /// clients.
    ///
    /// Off (<c>SafeZoneGridTracking</c>), zones made from then on get the game's layer 15; a zone that
    /// already has layer 1 is still looked after here until it rebuilds its physics or the server restarts.
    /// </summary>
    [PatchShim]
    public static class SafeZoneGridTracking
    {
        /// <summary>The layer the zone phantoms are put on; the game has nothing on it.</summary>
        public const int ZoneLayer = 1;

        /// <summary>The layer the game gives a zone phantom (the dynamic grids' layer).</summary>
        private const int GameZoneLayer = 15;

        /// <summary>Grid layers: static, dynamic, dynamic doubled, a grid's second body.</summary>
        private static readonly int[] GridLayers = { 13, 15, 16, 17 };

        /// <summary>Each zone looks for grids once in this many frames.</summary>
        public const int Period = 10;

        /// <summary>
        /// A grid already inside and across the border has its shape tested against the zone once in this
        /// many of its zone's passes - it would have left the zone when its last part left it.
        /// </summary>
        private const int BorderRecheckPasses = 6;

        private static Func<MySafeZone, MyConcurrentHashSet<IMyCubeGrid>> _grids;
        private static Func<MySafeZone, MyConcurrentHashSet<long>> _contained;
        private static Func<MySafeZone, MyEntity, bool> _insert;
        private static Action<MySafeZone, long> _sendInserted;
        private static Action<MySafeZone, MyEntity> _removed;

        private static readonly List<MyEntity> Found = new List<MyEntity>();
        private static readonly HashSet<long> Near = new HashSet<long>();
        private static readonly List<IMyCubeGrid> Inside = new List<IMyCubeGrid>();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("SafeZoneGridTracking", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            _grids = Accessors.Field<MySafeZone, MyConcurrentHashSet<IMyCubeGrid>>("m_grids");
            _contained = Accessors.Field<MySafeZone, MyConcurrentHashSet<long>>("m_containedEntities");
            _insert = Accessors.Method<MySafeZone, Func<MySafeZone, MyEntity, bool>>("InsertEntityInternal");
            _sendInserted = Accessors.Method<MySafeZone, Action<MySafeZone, long>>("SendInsertedEntity");
            _removed = Accessors.Method<MySafeZone, Action<MySafeZone, MyEntity>>("RemovedByPhysics");

            var filters = typeof(MyPhysics).GetMethod("InitCollisionFilters", BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(filters).Suffixes.Add(typeof(SafeZoneGridTracking).GetMethod(nameof(InitCollisionFiltersSuffix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var create = typeof(MyPhysicsBody).GetMethod(nameof(MyPhysicsBody.CreateFromCollisionObject),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            ctx.GetPattern(create).Prefixes.Add(typeof(SafeZoneGridTracking).GetMethod(nameof(CreateFromCollisionObjectPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>Layer 1: whatever layer 15 does not collide with, and no grid layer.</summary>
        internal static void ConfigureZoneLayer(HkWorld world)
        {
            var game = HkGroupFilter.CalcFilterInfo(GameZoneLayer, 0);
            for (var layer = 0; layer < 32; layer++)
            {
                if (layer != GameZoneLayer && !world.IsCollisionEnabled(game, HkGroupFilter.CalcFilterInfo(layer, 0)))
                    world.DisableCollisionsBetween(ZoneLayer, layer);
            }
            foreach (var layer in GridLayers) world.DisableCollisionsBetween(ZoneLayer, layer);
            world.DisableCollisionsBetween(ZoneLayer, ZoneLayer);
        }

        private static void InitCollisionFiltersSuffix(HkWorld world) => ConfigureZoneLayer(world);

        private static void CreateFromCollisionObjectPrefix(MyPhysicsBody __instance, ref int collisionFilter)
        {
            if (collisionFilter == GameZoneLayer && __instance.IsPhantom && __instance.Entity is MySafeZone &&
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config.SafeZoneGridTracking)
                collisionFilter = ZoneLayer;
        }

        /// <summary>Called every frame; each zone on layer 1 takes its turn once in <see cref="Period"/> frames.</summary>
        public static void Tick()
        {
            if (_grids == null || MySandboxGame.Static == null || MySessionComponentSafeZones.SafeZones.Count == 0) return;
            var frame = MySandboxGame.Static.SimulationFrameCounter;
            foreach (var zone in MySessionComponentSafeZones.SafeZones)
            {
                if ((ulong)(zone.EntityId + (long)frame) % Period != 0) continue;
                var body = zone.Physics?.RigidBody;
                if (body == null || body.Layer != ZoneLayer || zone.MarkedForClose) continue;
                try
                {
                    Track(zone, frame);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "SafeZoneGridTracking " + zone.EntityId);
                }
            }
        }

        /// <summary>
        /// A dynamic grid leaving the world leaves the zones on layer 1 too. The game learns it from the
        /// phantom (the body leaves the world); without it the grid's id would stay among the zone's
        /// contents for good and keep the zone updating every frame.
        /// </summary>
        public static void OnGridRemoved(MyCubeGrid grid)
        {
            if (_grids == null || grid.IsStatic || MySessionComponentSafeZones.SafeZones.Count == 0) return;
            foreach (var zone in MySessionComponentSafeZones.SafeZones)
            {
                if (zone.Physics?.RigidBody?.Layer != ZoneLayer) continue;
                if (_contained(zone).Contains(grid.EntityId) || _grids(zone).Contains(grid)) _removed(zone, grid);
            }
        }

        private static void Track(MySafeZone zone, ulong frame)
        {
            var sphere = zone.Shape == MySafeZoneShape.Sphere;
            var zoneMatrix = zone.PositionComp.WorldMatrixRef;
            var zoneSphere = new BoundingSphereD(zoneMatrix.Translation, zone.Radius);
            var zoneBox = new MyOrientedBoundingBoxD(zone.PositionComp.LocalAABB, zoneMatrix);

            Found.Clear();
            Near.Clear();
            if (sphere)
            {
                MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref zoneSphere, Found);
            }
            else
            {
                var aabb = zone.PositionComp.WorldAABB;
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref aabb, Found);
            }

            var inside = _grids(zone);
            var pass = frame / Period;
            foreach (var entity in Found)
            {
                if (!(entity is MyCubeGrid grid) || grid.IsStatic || grid.MarkedForClose) continue;
                var physics = grid.Physics;
                if (physics == null || !physics.IsInWorld || physics.RigidBody == null) continue;
                if (physics.ShapeChangeInProgress)
                {
                    // the zone keeps what it has until the shape is done
                    Near.Add(grid.EntityId);
                    continue;
                }

                var gridBox = new MyOrientedBoundingBoxD(grid.PositionComp.LocalAABB, grid.PositionComp.WorldMatrixRef);
                var containment = sphere ? zoneSphere.Contains(gridBox) : zoneBox.Contains(ref gridBox);
                if (containment == ContainmentType.Disjoint) continue;
                Near.Add(grid.EntityId);

                var isInside = inside.Contains(grid);
                if (containment == ContainmentType.Contains)
                {
                    if (!isInside) Insert(zone, grid);
                    continue;
                }

                // across the border: the shape decides, as it does for the game's own checks
                if (isInside && (pass + (ulong)grid.EntityId) % BorderRecheckPasses != 0) continue;
                var reaches = Penetrates(zone, grid);
                if (reaches && !isInside) Insert(zone, grid);
                else if (!reaches && isInside) _removed(zone, grid);
            }
            Found.Clear();

            Inside.Clear();
            foreach (var grid in inside) Inside.Add(grid);
            foreach (var grid in Inside)
            {
                if (grid is MyCubeGrid cubeGrid && !cubeGrid.IsStatic && !cubeGrid.MarkedForClose && !Near.Contains(cubeGrid.EntityId))
                    _removed(zone, cubeGrid);
            }
            Inside.Clear();
            Near.Clear();
        }

        private static void Insert(MySafeZone zone, MyCubeGrid grid)
        {
            if (_insert(zone, grid)) _sendInserted(zone, grid.EntityId);
        }

        /// <summary>The test the game makes when a body leaves a zone: does the grid's shape reach into the zone's.</summary>
        private static bool Penetrates(MySafeZone zone, MyCubeGrid grid)
        {
            var body = grid.Physics.RigidBody;
            var zoneBody = zone.Physics?.RigidBody;
            if (body == null || zoneBody == null) return false;
            var shape = body.GetShape();
            if (!shape.IsValid) return false;
            var position = grid.Physics.ClusterToWorld(body.Position);
            var rotation = Quaternion.CreateFromRotationMatrix(body.GetRigidBodyMatrix());
            var zonePosition = zone.PositionComp.GetPosition();
            var zoneRotation = Quaternion.CreateFromRotationMatrix(zone.PositionComp.GetOrientation());
            return MyPhysics.IsPenetratingShapeShape(shape, ref position, ref rotation, zoneBody.GetShape(), ref zonePosition, ref zoneRotation);
        }
    }
}
