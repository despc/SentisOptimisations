using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Torch.Managers.PatchManager;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// The bodies of a physics cluster the game does not step are not walked after the step.
    ///
    /// With "selective physics updates" (<c>EnableSelectivePhysicsUpdates</c>) a dedicated server steps Havok only in the
    /// clusters where there is a character or an entity replicated to some client (<c>MyPhysics.IsClusterActive</c>);
    /// elsewhere nothing moves. But <c>MyPhysics.UpdateActiveRigidBodies</c> still walked the active bodies of every
    /// cluster, every frame: for each grid its world matrix set again (with all the position listeners: replication,
    /// gyros, the pruning structure, the children), its accelerations, its place in the cluster tree. And since a world
    /// that is not stepped never puts its bodies to sleep either, every ship that was moving when the last player left
    /// stayed in that list for good: ~1000 grids walked each frame on a big world with nobody around, 20 s of 150 s
    /// (dotTrace) for bodies standing still.
    ///
    /// Here the walk takes only the clusters the step takes. When a cluster is stepped again, its bodies are walked from
    /// that frame on as before: they did not move in between, so there is nothing to catch up.
    ///
    /// And no force acts in a cluster that is not stepped. A force or an impulse (thrusters, gyroscopes, the propulsion
    /// of wheels: <c>MyPhysicsBody.AddForceInternal</c>) changes the velocity of the Havok body right away, and only the
    /// step turns velocity into movement: without the step the velocity just grew, frame after frame. A rover left
    /// driving came back with 50-90 m/s it had gathered standing still and was thrown on the first stepped frame; a ship
    /// hanging on its thrusters gathered 5 m/s (physics_resume, the same with the game's own walk). Time stands still
    /// there, and so do the forces. The drive of a wheel (<c>MyMotorSuspension.Accelerate</c>) turns its wheel with an
    /// impulse of its own, past AddForce: a wheel stood there spinning up to its speed limit and grabbed the ground at
    /// 9-12 m/s on the return; it waits too.
    ///
    /// Nor does a piston move its head there. A piston's own update moves where its head should be
    /// (<c>MyPistonBase.UpdatePosition</c>, velocity / 60 each frame) whatever the physics does, and the step pulls the
    /// head after it. Without the step the head stayed and the target ran to the limit; on the first stepped frame the
    /// solver pulled every head of a moving stack at once, at 22 m/s, and tore pistons off (physics_resume).
    ///
    /// And a character nobody controls does not keep its cluster stepped. The game steps a cluster with any
    /// character body in it - and a player who quits leaves the character standing where it was
    /// (<c>RemovePlayer(removeCharacter: false)</c>), as do the SentisAi bots when they are switched off. Two such
    /// bodies on the old server kept two clusters stepped for nobody, one of them with 183 active bodies. Only the
    /// characters someone controls count now (a player on line, a running bot, an animal); the others stand
    /// still like the rest of a cluster that is not stepped, until the player comes back and takes them.
    ///
    /// Nor does an unlocked landing gear there look for something to lock to. Every working gear that is not locked
    /// queues a box query into Havok every 10 frames (<c>MyLandingGear.FindBodyParallel</c>); where nothing moves the
    /// answer cannot change - 2.9 s of 185 on the old server. A gear ready to lock is left to the game.
    ///
    /// And when no cluster is stepped at all, the step is not started: <c>MyPhysics.StepWorldsParallel</c> woke
    /// Havok's thread pool, ran an empty job queue and waited for it every frame - ~1 ms a frame on a server with
    /// nobody on it.
    /// </summary>
    [PatchShim]
    public static class SelectivePhysicsBodies
    {
        private static Func<MyPhysics, int, int, bool> _isClusterActive;
        private static Action<MyPhysics, HkWorld> _iterateBodies;
        private static Func<MyPhysics, List<MyPhysicsBody>> _iterationBodies;
        private static Func<MyPhysics, object> _observer;
        private static Func<MyPhysics, bool> _updateKinematic;
        /// <summary>The Havok worlds the game did not step this frame; replaced whole, read from the parallel updates.</summary>
        private static volatile HashSet<HkWorld> _unstepped = new HashSet<HkWorld>();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("SelectivePhysicsBodies", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(MyPhysics);
            MethodInfo M(string name, params Type[] args) => type.GetMethod(name, any, null, args, null) ?? throw new MissingMethodException(type.Name, name);
            FieldInfo F(string name) => type.GetField(name, any) ?? throw new MissingFieldException(type.Name, name);

            _isClusterActive = (Func<MyPhysics, int, int, bool>)Delegate.CreateDelegate(typeof(Func<MyPhysics, int, int, bool>), M("IsClusterActive", typeof(int), typeof(int)));
            _iterateBodies = (Action<MyPhysics, HkWorld>)Delegate.CreateDelegate(typeof(Action<MyPhysics, HkWorld>), M("IterateBodies", typeof(HkWorld)));
            _iterationBodies = Getter<List<MyPhysicsBody>>(F("m_iterationBodies"));
            _observer = Getter<object>(F("m_worldObserver"));
            _updateKinematic = Getter<bool>(F("m_updateKinematicBodies"));

            ctx.GetPattern(M("UpdateActiveRigidBodies")).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            var addForce = typeof(MyPhysicsBody).GetMethod("AddForceInternal", any) ?? throw new MissingMethodException(nameof(MyPhysicsBody), "AddForceInternal");
            ctx.GetPattern(addForce).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(AddForcePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var accelerate = typeof(Sandbox.Game.Entities.Cube.MyMotorSuspension).GetMethod("Accelerate", any, null, new[] { typeof(float), typeof(bool) }, null)
                             ?? throw new MissingMethodException("MyMotorSuspension", "Accelerate");
            ctx.GetPattern(accelerate).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(WheelDrivePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var gearType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SpaceEngineers.Game.Entities.Blocks.MyLandingGear", false)).FirstOrDefault(t => t != null)
                           ?? throw new TypeLoadException("MyLandingGear");
            var findBody = gearType.GetMethod("FindBodyParallel", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyLandingGear", "FindBodyParallel");
            ctx.GetPattern(findBody).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(GearFindBodyPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(M("StepWorldsParallel")).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(StepPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            ctx.GetPattern(M("IsClusterActive", typeof(int), typeof(int))).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(ClusterActivePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var pistonMove = typeof(Sandbox.Game.Entities.Blocks.MyPistonBase).GetMethod("UpdatePosition", any, null, new[] { typeof(bool) }, null)
                             ?? throw new MissingMethodException("MyPistonBase", "UpdatePosition");
            ctx.GetPattern(pistonMove).Prefixes.Add(typeof(SelectivePhysicsBodies).GetMethod(nameof(PistonMovePrefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Func<MyPhysics, T> Getter<T>(FieldInfo field)
        {
            var arg = Expression.Parameter(typeof(MyPhysics), "physics");
            return Expression.Lambda<Func<MyPhysics, T>>(Expression.Convert(Expression.Field(arg, field), typeof(T)), arg).Compile();
        }

        private static readonly HashSet<HkWorld> Empty = new HashSet<HkWorld>();

        // the characters someone controls, by cluster, counted once a frame
        private static readonly Dictionary<int, int> _controlled = new Dictionary<int, int>();
        private static ulong _controlledFrame = ulong.MaxValue;

        /// <summary>The characters of a cluster, for the game's choice to step it: only the ones someone controls.</summary>
        private static void ClusterActivePrefix(int clusterId, ref int rigidBodiesCount)
        {
            if (rigidBodiesCount == 0) return;
            try
            {
                var frame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0UL;
                if (frame != _controlledFrame)
                {
                    _controlledFrame = frame;
                    _controlled.Clear();
                    foreach (var cluster in MyPhysics.Clusters.GetClusters())
                        if (cluster.UserData is HkWorld world && world.CharacterRigidBodies.Count > 0)
                            _controlled[cluster.ClusterId] = Controlled(world);
                }
                rigidBodiesCount = _controlled.TryGetValue(clusterId, out var count) ? count : rigidBodiesCount;
            }
            catch (Exception)
            {
                // the game's own count stands
            }
        }

        private static int Controlled(HkWorld world)
        {
            var count = 0;
            foreach (var body in world.CharacterRigidBodies)
            {
                var character = (body.GetHitRigidBody()?.UserObject as MyPhysicsBody)?.Entity as Sandbox.Game.Entities.Character.MyCharacter;
                if (character == null || Stepped(character)) count++;   // not a character the way it looks: as the game counts it
            }
            return count;
        }

        /// <summary>Whether a character keeps its cluster stepped: alive and controlled (a player on line, a bot, an animal).</summary>
        public static bool Stepped(Sandbox.Game.Entities.Character.MyCharacter character) =>
            Stepped(character.IsDead, character.ControllerInfo?.Controller != null);

        public static bool Stepped(bool dead, bool controlled) => !dead && controlled;

        /// <summary>A force on a body in a world that is not stepped is left out.</summary>
        private static bool AddForcePrefix(MyPhysicsBody __instance)
        {
            var unstepped = _unstepped;
            if (unstepped.Count == 0) return true;
            try
            {
                var world = __instance.HavokWorld;
                return world == null || !unstepped.Contains(world);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>No step at all when no cluster would be stepped (with selective updates).</summary>
        private static bool StepPrefix(MyPhysics __instance)
        {
            try
            {
                if (_observer(__instance) == null) return true;
                foreach (var cluster in MyPhysics.Clusters.GetClusters())
                    if (cluster.UserData is HkWorld world && _isClusterActive(__instance, cluster.ClusterId, world.CharacterRigidBodies.Count))
                        return true;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>An unlocked gear in a world that is not stepped does not look for something to lock to.</summary>
        private static bool GearFindBodyPrefix(Sandbox.Game.Entities.MyCubeBlock __instance)
        {
            try
            {
                if (!(__instance is SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear gear)) return true;
                return gear.LockMode != SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Unlocked || !InUnstepped(__instance.CubeGrid);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>A wheel on a grid in a world that is not stepped is not driven.</summary>
        private static bool WheelDrivePrefix(Sandbox.Game.Entities.Cube.MyMotorSuspension __instance) => !InUnstepped(__instance.CubeGrid);

        private static bool InUnstepped(Sandbox.Game.Entities.MyCubeGrid grid)
        {
            var unstepped = _unstepped;
            if (unstepped.Count == 0) return false;
            try
            {
                var world = (grid?.Physics as MyPhysicsBody)?.HavokWorld;
                return world != null && unstepped.Contains(world);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A piston on a grid in a world that is not stepped keeps its head where it is (not the forced update of a load or an attach).</summary>
        private static bool PistonMovePrefix(Sandbox.Game.Entities.Blocks.MyPistonBase __instance, bool forceUpdate) =>
            forceUpdate || !InUnstepped(__instance.CubeGrid);

        /// <summary>Whether the walk after the step takes a cluster's bodies.</summary>
        public static bool Walk(bool selective, bool clusterStepped) => !selective || clusterStepped;

        private static bool Prefix(MyPhysics __instance)
        {
            try
            {
                // no observer: the game steps every cluster, and so does its walk
                if (_observer(__instance) == null) return true;

                var bodies = _iterationBodies(__instance);
                HashSet<HkWorld> unstepped = null;
                foreach (var cluster in MyPhysics.Clusters.GetClusters())
                {
                    if (!(cluster.UserData is HkWorld world)) continue;
                    if (Walk(true, _isClusterActive(__instance, cluster.ClusterId, world.CharacterRigidBodies.Count)))
                        _iterateBodies(__instance, world);
                    else
                        (unstepped ?? (unstepped = new HashSet<HkWorld>())).Add(world);
                }
                _unstepped = unstepped ?? Empty;

                // the rest of the game's own method, as it is
                var kinematic = _updateKinematic(__instance);
                MyPhysics.Clusters.SuppressClusterReorder = true;
                try
                {
                    foreach (var body in bodies)
                    {
                        if (kinematic && body.IsKinematic)
                            body.OnMotionKinematic();
                        else
                            body.OnMotionDynamic();
                    }
                }
                finally
                {
                    MyPhysics.Clusters.SuppressClusterReorder = false;
                }
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "SelectivePhysicsBodies");
                return true;
            }
        }
    }
}
