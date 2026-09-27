using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A wild animal spawned half a second after its place is chosen, with the ground's physics made meanwhile.
    ///
    /// <c>MySpaceFaunaComponent.SpawnBot</c> picks a random place 100-odd metres from a player and looks for room there
    /// (<c>MyEntities.FindFreePlace</c>): nobody has been there, the planet's physics shapes of that ground do not exist yet,
    /// and the physics makes them on the spot, inside the frame - 7-17 ms of a spawn on the stand. Here the place is
    /// chosen the same way (the same random disc, the ground under it), the shapes along the ground there are asked for
    /// in the background (<c>MyPlanet.PrefetchShapeOnRay</c>), and <see cref="SpawnDelayFrames"/> frames later the rest
    /// of the game's own spawn is done: the bot limit, the room, the spawn location, the animal and the settings. On the
    /// stand the room was then found in 0.2-2.6 ms instead of 5-17 ms. A pack spawned at once (three wolves were a
    /// 45 ms frame) goes one animal a frame.
    /// </summary>
    [PatchShim]
    public static class FaunaSpawnPrefetch
    {
        /// <summary>Frames between choosing the place and spawning there.</summary>
        public const int SpawnDelayFrames = 30;

        /// <summary>Metres between the rays along which the ground's physics is asked for (a physics cell is 8 m).</summary>
        private const double PrefetchStep = 8;

        private sealed class Pending
        {
            public object Component;
            public MyPlanet Planet;
            public MyPlanetAnimalSpawnInfo Animals;
            public Vector3D At;
            public int Frame;
        }

        // game thread only
        private static readonly List<Pending> Waiting = new List<Pending>();
        private static FieldInfo _planets, _botNumber, _position;
        private static MethodInfo _animal;
        private static int _frame;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("FaunaSpawnPrefetch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var fauna = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SpaceEngineers.Game.AI.MySpaceFaunaComponent", false)).FirstOrDefault(t => t != null)
                        ?? throw new TypeLoadException("SpaceEngineers.Game.AI.MySpaceFaunaComponent");
            var spawnBot = fauna.GetMethod("SpawnBot", any) ?? throw new MissingMethodException("MySpaceFaunaComponent.SpawnBot");
            _planets = fauna.GetField("m_planets", any) ?? throw new MissingFieldException("MySpaceFaunaComponent.m_planets");
            _botNumber = fauna.GetNestedType("PlanetAIInfo", any)?.GetField("BotNumber", any) ?? throw new MissingFieldException("PlanetAIInfo.BotNumber");
            _position = fauna.GetNestedType("SpawnInfo", any)?.GetField("Position", any) ?? throw new MissingFieldException("SpawnInfo.Position");
            _animal = fauna.GetMethod("GetAnimalDefinition", any) ?? throw new MissingMethodException("MySpaceFaunaComponent.GetAnimalDefinition");
            ctx.GetPattern(spawnBot).Prefixes.Add(typeof(FaunaSpawnPrefetch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix(object __instance, object spawnInfo, MyPlanet planet, MyPlanetAnimalSpawnInfo animalSpawnInfo)
        {
            try
            {
                if (!UnderLimit(__instance, planet) || MySandboxGame.Static.LimitNPCsOnLowMemory) return true;   // the game says why, as before
                // the game's choice of the place, step by step
                var center = (Vector3D)_position.GetValue(spawnInfo);
                var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(center);
                if (gravity == Vector3D.Zero) gravity = Vector3D.Up;
                gravity.Normalize();
                var tangent = Vector3D.CalculatePerpendicularVector(gravity);
                var bitangent = Vector3D.Cross(gravity, tangent);
                tangent.Normalize();
                bitangent.Normalize();
                var at = MyUtils.GetRandomDiscPosition(ref center, animalSpawnInfo.SpawnDistMin, animalSpawnInfo.SpawnDistMax, ref tangent, ref bitangent);
                at = planet.GetClosestSurfacePointGlobal(ref at);
                // the ground around the place, where the search for room looks: vertical rays a physics cell apart
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                {
                    var over = at + tangent * (x * PrefetchStep) + bitangent * (y * PrefetchStep);
                    var ray = new LineD(over - gravity * 10, over + gravity * 10);
                    planet.PrefetchShapeOnRay(ref ray);
                }
                Waiting.Add(new Pending { Component = __instance, Planet = planet, Animals = animalSpawnInfo, At = at, Frame = _frame + SpawnDelayFrames });
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "FaunaSpawnPrefetch: the place could not be prepared; the game spawns as before");
                return true;
            }
        }

        private static bool UnderLimit(object component, MyPlanet planet)
        {
            var planets = (IDictionary)_planets.GetValue(component);
            if (!planets.Contains(planet.EntityId)) return false;
            return (int)_botNumber.GetValue(planets[planet.EntityId]) < planet.Generator.MaxBotCount;
        }

        /// <summary>
        /// Game thread, every frame: an animal whose ground has had time to be made spawned - one a frame, the pack the
        /// game asks for at once spread over the frames that follow (creating a bot is 5-8 ms of the frame after).
        /// </summary>
        public static void Tick()
        {
            _frame++;
            if (Waiting.Count == 0) return;
            var index = Waiting.FindIndex(p => p.Frame <= _frame);
            if (index < 0) return;
            var pending = Waiting[index];
            Waiting.RemoveAt(index);
            try
            {
                Spawn(pending);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "FaunaSpawnPrefetch: an animal could not be spawned");
            }
        }

        /// <summary>The rest of the game's <c>SpawnBot</c>, at the place chosen before.</summary>
        private static void Spawn(Pending pending)
        {
            var planet = pending.Planet;
            if (planet.MarkedForClose || !UnderLimit(pending.Component, planet) || MySandboxGame.Static.LimitNPCsOnLowMemory) return;
            var at = pending.At;
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(at);
            if (gravity == Vector3D.Zero) gravity = Vector3D.Up;
            gravity.Normalize();
            var tangent = Vector3D.CalculatePerpendicularVector(gravity);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var matrix = MatrixD.CreateWorld(at, (Vector3D)tangent, (Vector3D)(-gravity));
            var free = MyEntities.FindFreePlace(ref matrix, matrix.Up, 2f);
            if (free.HasValue) at = free.Value;
            planet.CorrectSpawnLocation(ref at, 2.0);
            if (!(_animal.Invoke(null, new object[] { pending.Animals }) is MyAgentDefinition agent)) return;
            var allowed = agent.WildlifeCategory == MyWildlifeCategory.Wolf ? MySession.Static.EnableWolfs
                : agent.WildlifeCategory == MyWildlifeCategory.Spider ? MySession.Static.EnableSpiders
                : true;
            if (!allowed) return;
            var placeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MyAIComponent.Static.SpawnNewBot(agent, at);
            if (global::SentisOptimisations.DiagLog.On) SentisOptimisationsPlugin.Log.Info($"FaunaSpawnPrefetch: {agent.Id.SubtypeName} spawned on {planet.StorageName}, its place found in {placeMs:0.0} ms");
        }
    }
}
