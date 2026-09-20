using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.Groups;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using Sandbox.Engine.Physics;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Safe zones: a grid is safe when any grid mechanically attached to it is, the zones of grids
    /// nobody is near update rarely, and the cost of leaving a zone is charged to the grid that
    /// causes it.
    ///
    /// <b>Subgrids.</b> Vanilla decides for the entity alone, so a rotor head or a piston top outside
    /// the zone is unprotected while the ship it belongs to is inside. <c>IsSafe</c> is replaced with
    /// a version that asks the game's own <c>IsSubGridSafe</c> for every grid of the mechanical group.
    /// It runs on every shot and every bit of damage, so the game's method is bound once into a
    /// delegate and its private result enum is mapped once - it used to be reflected, boxed and
    /// mapped through <c>ToString</c> on every call.
    ///
    /// <b>Leaving a zone.</b> <c>phantom_Leave</c> is timed, and a grid whose removal takes longer
    /// than <c>SafeZonePhysicsThreshold</c> is recorded, which is what the DDoS detector reports on.
    ///
    /// <b>Zone updates.</b> With <c>SlowdownEnabled</c>, a zone on a grid nobody is near updates once
    /// in ten or in a hundred frames, zones spread over those frames by a random start.
    /// </summary>
    [PatchShim]
    public static class SafezonePatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Grids that made leaving a safe zone expensive, and how much time they cost.</summary>
        public static readonly ConcurrentDictionary<long, GridInSzInfo> EntitiesInSZ =
            new ConcurrentDictionary<long, GridInSzInfo>();

        private static readonly ConcurrentDictionary<long, int> Cooldowns = new ConcurrentDictionary<long, int>();
        private static readonly Random Random = new Random();

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly Action<MySafeZone, HkRigidBody, IMyEntity> RemoveEntityPhantom =
            Accessors.Method<MySafeZone, Action<MySafeZone, HkRigidBody, IMyEntity>>("RemoveEntityPhantom");
        private static readonly MethodInfo IsSubGridSafeMethod = typeof(MySafeZone).GetMethod("IsSubGridSafe", Instance);

        /// <summary>The private result of IsSubGridSafe, by its underlying value.</summary>
        private static readonly Dictionary<int, SubgridCheckResult> ResultByValue = new Dictionary<int, SubgridCheckResult>();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("SafezonePatch", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (RemoveEntityPhantom == null) throw new MissingMethodException("MySafeZone.RemoveEntityPhantom");
            if (IsSubGridSafeMethod == null) throw new MissingMethodException("MySafeZone.IsSubGridSafe");
            MapSubgridResults();

            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            var self = typeof(SafezonePatch);

            ctx.GetPattern(typeof(MySafeZone).GetMethod("phantom_Leave", Instance))
                .Prefixes.Add(self.GetMethod(nameof(PhantomLeavePatched), statics));
            ctx.GetPattern(typeof(MySafeZone).GetMethod("IsSafe", Instance))
                .Prefixes.Add(self.GetMethod(nameof(IsSafePatched), statics));
            ctx.GetPattern(typeof(MySafeZone).GetMethod(nameof(MySafeZone.UpdateBeforeSimulation),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Prefixes.Add(self.GetMethod(nameof(UpdateBeforeSimulationPatched), statics));
        }

        /// <summary>
        /// The names of the game's enum are matched to ours once, so the result of every check is a
        /// dictionary lookup by value instead of a string comparison.
        /// </summary>
        private static void MapSubgridResults()
        {
            var type = IsSubGridSafeMethod.ReturnType;
            if (!type.IsEnum) throw new InvalidOperationException("MySafeZone.IsSubGridSafe no longer returns an enum");
            foreach (var value in Enum.GetValues(type))
            {
                var name = Enum.GetName(type, value)?.Replace("_", "").ToUpperInvariant();
                SubgridCheckResult mapped;
                switch (name)
                {
                    case "NOTSAFE": mapped = SubgridCheckResult.NotSafe; break;
                    case "NEEDEXTRACHECK": mapped = SubgridCheckResult.NeedExtraCheck; break;
                    case "SAFE": mapped = SubgridCheckResult.Safe; break;
                    case "ADMIN": mapped = SubgridCheckResult.Admin; break;
                    default: continue;
                }

                ResultByValue[(int)value] = mapped;
            }

            if (ResultByValue.Count == 0)
                throw new InvalidOperationException("MySafeZone.IsSubGridSafe returns an enum nothing maps to");
        }

        /// <summary>
        /// Removes the body from the zone and charges the time it took to the grid, so a grid built
        /// to make leaving a zone expensive can be found.
        /// </summary>
        private static bool PhantomLeavePatched(MySafeZone __instance, HkPhantomCallbackShape sender, HkRigidBody body)
        {
            try
            {
                var entity = body.GetEntity(0U);
                if (entity == null) return false;

                var startedAt = Stopwatch.GetTimestamp();
                RemoveEntityPhantom(__instance, body, entity);
                if (!(entity is MyCubeGrid grid)) return false;

                var ms = (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);
                if (ms < SentisOptimisationsPlugin.Config.SafeZonePhysicsThreshold) return false;

                EntitiesInSZ.AddOrUpdate(entity.EntityId, _ => new GridInSzInfo(grid, ms), (_, known) =>
                {
                    known.DDosTimeMs += ms;
                    return known;
                });
            }
            catch (Exception e)
            {
                Log.Warn(e, "phantom_Leave patch failed");
            }

            return false;
        }

        /// <summary>A zone on a grid nobody is near does not need to update every frame.</summary>
        private static bool UpdateBeforeSimulationPatched(MySafeZone __instance)
        {
            try
            {
                if (!SentisOptimisationsPlugin.Config.SlowdownEnabled) return true;

                var blockId = __instance.SafeZoneBlockId;
                if (blockId == 0) return true;
                if (!MyEntities.TryGetEntityById<MyCubeBlock>(blockId, out var block)) return true;

                var grid = block.CubeGrid;
                switch (grid.PlayerPresenceTier)
                {
                    case MyUpdateTiersPlayerPresence.Tier1: return !NeedSkip(grid.EntityId, 10);
                    case MyUpdateTiersPlayerPresence.Tier2: return !NeedSkip(grid.EntityId, 100);
                    default: return true;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Safe zone update patch failed");
                return true;
            }
        }

        /// <summary>
        /// Whether the zone protects the entity. Same answers as vanilla, except that a grid is safe
        /// when any grid of its mechanical group is.
        /// </summary>
        private static bool IsSafePatched(MySafeZone __instance, MyEntity entity, ref bool __result)
        {
            if (!SentisOptimisationsPlugin.Config.SafeZoneSubGridOptimisation) return true;

            try
            {
                if (entity is MyFloatingObject || entity is MyInventoryBagEntity)
                {
                    __result = __instance.Entities.Contains(entity.EntityId)
                        ? __instance.AccessTypeFloatingObjects == MySafeZoneAccess.Whitelist
                        : (uint)__instance.AccessTypeFloatingObjects > 0U;
                    return false;
                }

                var topMostParent = entity.GetTopMostParent(null);
                if (topMostParent is IMyComponentOwner<MyIDModule> owner && owner.GetComponent(out MyIDModule id))
                {
                    __result = IsOwnerSafe(__instance, id);
                    return false;
                }

                if (topMostParent is MyCubeGrid grid)
                {
                    __result = IsGroupSafe(__instance, grid);
                    return false;
                }

                if ((entity is MyAmmoBase || entity is MyMeteor) &&
                    (__instance.AllowedActions & MySafeZoneAction.Shooting) == 0)
                {
                    __result = false;
                    return false;
                }

                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.Warn(e, "IsSafe patch failed; falling back to vanilla");
                return true;
            }
        }

        private static bool IsOwnerSafe(MySafeZone zone, MyIDModule id)
        {
            var steamId = MySession.Static.Players.TryGetSteamId(id.Owner);
            if (steamId != 0UL && MySafeZone.CheckAdminIgnoreSafezones(steamId)) return true;

            if (zone.Players.Contains(id.Owner)) return zone.AccessTypePlayers == MySafeZoneAccess.Whitelist;

            if (MySession.Static.Factions.TryGetPlayerFaction(id.Owner) is MyFaction faction &&
                zone.Factions.Contains(faction))
            {
                return zone.AccessTypeFactions == MySafeZoneAccess.Whitelist;
            }

            return zone.AccessTypePlayers == MySafeZoneAccess.Blacklist;
        }

        /// <summary>
        /// Safe as soon as one grid of the mechanical group is. NEED_EXTRA_CHECK counts as not safe,
        /// which is what the previous version did too - it gathered that case into a variable and then
        /// never looked at it.
        /// </summary>
        private static bool IsGroupSafe(MySafeZone zone, MyCubeGrid grid)
        {
            var nodes = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Mechanical).GetGroupNodes(grid);
            if (nodes == null || nodes.Count == 0) return IsSubGridSafe(zone, grid) >= SubgridCheckResult.Safe;

            foreach (var node in nodes)
                if (IsSubGridSafe(zone, node) >= SubgridCheckResult.Safe)
                    return true;

            return false;
        }

        private static SubgridCheckResult IsSubGridSafe(MySafeZone zone, MyCubeGrid grid)
        {
            var result = IsSubGridSafeMethod.Invoke(zone, new object[] { grid });
            return ResultByValue.TryGetValue((int)result, out var mapped) ? mapped : SubgridCheckResult.NotSafe;
        }

        /// <summary>
        /// True while the zone is inside its cooldown. The first cooldown starts at a random point, so
        /// the zones of a world do not all update on the same frame.
        /// </summary>
        private static bool NeedSkip(long id, int period)
        {
            if (!Cooldowns.TryGetValue(id, out var cooldown))
            {
                Cooldowns[id] = Random.Next(0, period);
                return true;
            }

            if (cooldown > period)
            {
                Cooldowns[id] = 0;
                return false;
            }

            Cooldowns[id] = cooldown + 1;
            return true;
        }

        /// <summary>Forgets what is remembered about an entity that is gone.</summary>
        public static void CleanupEntity(MyEntity entity)
        {
            if (entity == null) return;
            EntitiesInSZ.TryRemove(entity.EntityId, out _);
            Cooldowns.TryRemove(entity.EntityId, out _);
        }

        private enum SubgridCheckResult
        {
            NotSafe,
            NeedExtraCheck,
            Safe,
            Admin
        }
    }

    public class GridInSzInfo
    {
        public MyCubeGrid MyCubeGrid;
        public long DDosTimeMs;

        public GridInSzInfo(MyCubeGrid myCubeGrid, long dDosTimeMs)
        {
            MyCubeGrid = myCubeGrid;
            DDosTimeMs = dDosTimeMs;
        }
    }
}
