using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The economy's station cells made only where a station is.
    ///
    /// Making a cell of the station generator (<c>MyStationCellGenerator.GenerateProceduralCell</c>) goes through every
    /// station of every faction to find the ones inside it, and the cell is kept only if there is one. A respawn in a
    /// drop pod asks for the stations within 150 km of the pod (for the datapad in its seat): on the stand that was 2349
    /// cells, 14.9 ms of the 21.7 ms the pod's seat took. Here the cells that hold a station are known from a set built
    /// at most once a frame; a cell without one gets nothing at once - what the game would have found - and a cell with
    /// one is made by the game as before.
    /// </summary>
    [PatchShim]
    public static class StationCellIndex
    {
        // game thread: the cells with a station in them, per cell size, and the frame they were worked out in
        private static readonly Dictionary<double, HashSet<Vector3I>> Cells = new Dictionary<double, HashSet<Vector3I>>();
        private static int _frame = -1;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("StationCellIndex", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var generate = typeof(MyStationCellGenerator).GetMethod("GenerateProceduralCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException("MyStationCellGenerator.GenerateProceduralCell");
            ctx.GetPattern(generate).Prefixes.Add(typeof(StationCellIndex).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static bool Prefix(MyStationCellGenerator __instance, ref Vector3I cellId, ref MyProceduralCell __result)
        {
            var session = MySession.Static;
            if (session?.Settings == null || !session.Settings.EnableEconomy || session.Factions == null) return true;
            var frame = session.GameplayFrameCounter;
            if (frame != _frame)
            {
                _frame = frame;
                Cells.Clear();
            }
            var size = __instance.CELL_SIZE;
            if (!Cells.TryGetValue(size, out var cells))
            {
                cells = new HashSet<Vector3I>();
                foreach (var faction in session.Factions)
                foreach (var station in faction.Value.Stations)
                    AddCellsOf(station.Position, size, cells);
                Cells[size] = cells;
            }
            if (cells.Contains(cellId)) return true;
            __result = null;
            return false;
        }

        /// <summary>
        /// The cells whose box holds the point, borders included (the game's test is the box's <c>Contains</c>): one,
        /// or the neighbours too for a point on a border.
        /// </summary>
        public static void AddCellsOf(Vector3D position, double size, HashSet<Vector3I> cells)
        {
            int Low(double v) => (int)Math.Floor(v / size);
            bool OnBorder(double v) => v / size == Math.Floor(v / size);
            var x = Low(position.X);
            var y = Low(position.Y);
            var z = Low(position.Z);
            for (var dx = OnBorder(position.X) ? -1 : 0; dx <= 0; dx++)
            for (var dy = OnBorder(position.Y) ? -1 : 0; dy <= 0; dy++)
            for (var dz = OnBorder(position.Z) ? -1 : 0; dz <= 0; dz++)
                cells.Add(new Vector3I(x + dx, y + dy, z + dz));
        }
    }
}
