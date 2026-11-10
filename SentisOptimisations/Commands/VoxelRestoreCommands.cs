using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace SentisOptimisations.Commands
{
    /// <summary>
    /// The admins' restore of the planets' terrain away from grids: <c>!voxelrestore &lt;m&gt; [delete]</c>, <c>!voxelrestore stop</c>.
    ///
    /// A planet's storage keeps only what was changed - dug, filled, painted - as leaves of 16 voxels (16 m) of its own;
    /// the rest is read from the planet's generator. Every changed leaf with no grid (not a projection) and no character
    /// within the distance of it goes back to the generator's terrain, as the voxel hand's revert does it (the clients
    /// told with the same event); the leaves near a grid stay as they are - mines under bases, foundations. The distance
    /// is counted from a leaf's box to a grid's box along each axis, so what stays is never nearer than asked, rather
    /// farther. Without <c>delete</c> only counts; with it restores a few leaves a frame (a grid come near meanwhile keeps
    /// its leaves), and says when done. Asteroids are not touched.
    /// </summary>
    public class VoxelRestoreCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Leaves restored a frame.</summary>
        public const int PerTick = 20;

        private const string Usage = "!voxelrestore <метров до гридов> [delete] - восстановить рельеф планет (изменённые куски по 16 м), " +
                                     "кроме мест ближе заданного к гридам и персонажам; без delete только считает; !voxelrestore stop - остановить";

        private struct Piece
        {
            public MyPlanet Planet;
            public Vector3I Min;   // storage voxels of the leaf
            public int Size;
        }

        private static readonly Queue<Piece> Queue = new Queue<Piece>();
        private static CommandContext _asker;
        private static double _distance;
        private static int _queued;
        private static string _who;

        /// <summary>For the stand's check: leaves restored and kept (a grid came near) by the runs since the start; of the last count.</summary>
        public static long Restored, KeptLate;
        public static int LastToRestore, LastKept;
        public static int Pending => Queue.Count;

        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo ContentLeaves = typeof(MyOctreeStorage).GetField("m_contentLeaves", Instance);
        private static readonly FieldInfo MaterialLeaves = typeof(MyOctreeStorage).GetField("m_materialLeaves", Instance);
        private static readonly FieldInfo StorageLock = typeof(MyStorageBase).GetField("StorageLock", Instance);
        private static readonly MethodInfo FlushCache = typeof(MyStorageBase).GetMethod("FlushCache", Instance, null, new[] { typeof(BoundingBoxI).MakeByRefType(), typeof(int) }, null);
        private static readonly MethodInfo PerformBox = typeof(MyVoxelBase).GetMethod("PerformVoxelOperationBox_Implementation", Instance);

        [Command("voxelrestore", Usage)]
        [Permission(MyPromoteLevel.Admin)]
        public void VoxelRestore()
        {
            var args = Context.Args;
            if (args.Count == 1 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                var left = Queue.Count;
                Queue.Clear();
                Log.Warn($"!voxelrestore stopped by {Context.Player?.DisplayName ?? "console"}, {left} pieces left");
                Context.Respond(left > 0 ? $"Остановлено, не восстановлено {left} кусков." : "Ничего не идёт.");
                return;
            }
            if (args.Count == 0 || !double.TryParse(args[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) ||
                double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0)
            {
                Context.Respond("Расстояние до гридов - число метров, 0 или больше.\n" + Usage);
                return;
            }
            var delete = false;
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i].Equals("delete", StringComparison.OrdinalIgnoreCase)) delete = true;
                else
                {
                    Context.Respond("Не понял \"" + args[i] + "\".\n" + Usage);
                    return;
                }
            }
            if (Queue.Count > 0)
            {
                Context.Respond($"Уже идёт восстановление: осталось {Queue.Count} из {_queued} кусков. Остановить: !voxelrestore stop");
                return;
            }
            Context.Respond(Run(distance, delete, Context, Context.Player?.DisplayName ?? "console"));
        }

        /// <summary>Counts the changed leaves of every planet, far and near; queues the far ones when told to delete (game thread).</summary>
        internal static string Run(double distance, bool delete, CommandContext asker, string who)
        {
            var far = new List<Piece>();
            var lines = new List<string>();
            var kept = 0;
            foreach (var planet in MyEntities.GetEntities().OfType<MyPlanet>().ToList())
            {
                if (planet.MarkedForClose || !(planet.Storage is MyOctreeStorage storage)) continue;
                int planetFar = 0, planetNear = 0;
                foreach (var (min, size) in Changed(storage))
                {
                    var piece = new Piece { Planet = planet, Min = min, Size = size };
                    if (Near(piece, distance)) planetNear++;
                    else
                    {
                        planetFar++;
                        far.Add(piece);
                    }
                }
                kept += planetNear;
                if (planetFar + planetNear > 0)
                    lines.Add($"  {planet.StorageName}: восстановить {planetFar}, оставить {planetNear}");
            }
            LastToRestore = far.Count;
            LastKept = kept;
            var text = (delete ? "Восстанавливаю" : "Будет восстановлено") + $": {far.Count} кусков по 16 м, оставлено у гридов {kept} " +
                       $"(дальше {PcuCommands.N(distance)} м от гридов и персонажей)" + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
            if (!delete)
                return text + (far.Count > 0 ? "\nЧтобы восстановить, повторите команду с delete." : "");
            if (far.Count == 0) return text;
            foreach (var piece in far) Queue.Enqueue(piece);
            _queued = far.Count;
            _asker = asker;
            _distance = distance;
            _who = who;
            Log.Warn($"!voxelrestore by {who}: restoring {far.Count} pieces of 16 m farther than {distance} m from grids, keeping {kept}; " + string.Join("; ", lines.Select(l => l.Trim())));
            return text + $"\nПо {PerTick} кусков за кадр, около {Math.Ceiling(far.Count / (PerTick * 60.0))} с.";
        }

        /// <summary>The queued leaves, a few a frame (the plugin's update, game thread).</summary>
        public static void Tick()
        {
            if (Queue.Count == 0) return;
            for (var i = 0; i < PerTick && Queue.Count > 0; i++)
            {
                var piece = Queue.Dequeue();
                try
                {
                    if (piece.Planet.MarkedForClose || piece.Planet.Storage == null) continue;
                    if (Near(piece, _distance))
                    {
                        KeptLate++;
                        continue;
                    }
                    Restore(piece);
                    Restored++;
                }
                catch (Exception e)
                {
                    Log.Error(e, "!voxelrestore: a piece at " + piece.Min + " of " + piece.Planet.StorageName);
                }
            }
            if (Queue.Count > 0) return;
            Log.Warn($"!voxelrestore by {_who}: done, {_queued} pieces queued (restored {Restored}, kept as a grid came near {KeptLate} since the start)");
            try { _asker?.Respond($"Восстановление рельефа закончено: {_queued} кусков."); }
            catch (Exception) { }
            _asker = null;
        }

        /// <summary>The leaves of the storage that are not the generator's: changed (both content and material).</summary>
        private static List<(Vector3I Min, int Size)> Changed(MyOctreeStorage storage)
        {
            // writes not yet in the tree (the storage's cache) first, lest a fresh change be missed
            Flush(storage, new BoundingBoxI(Vector3I.Zero, storage.Size));
            var found = new HashSet<(Vector3I, int)>();
            var guard = (FastResourceLock)StorageLock.GetValue(storage);
            using (guard.AcquireSharedUsing())
            {
                foreach (var field in new[] { ContentLeaves, MaterialLeaves })
                foreach (var leaf in (System.Collections.IDictionary)field.GetValue(storage))
                {
                    var entry = (System.Collections.DictionaryEntry)leaf;
                    if (entry.Value == null || entry.Value.GetType().Name == "MyProviderLeaf") continue;
                    var cell = new MyCellCoord();
                    cell.SetUnpack((ulong)entry.Key);
                    cell.Lod += 4;
                    found.Add((cell.CoordInLod << cell.Lod, 1 << cell.Lod));
                }
            }
            return found.ToList();
        }

        private static void Flush(MyVoxelBase planet, Vector3I min, int size) => Flush((MyStorageBase)planet.Storage, new BoundingBoxI(min, min + size));

        private static void Flush(MyStorageBase storage, BoundingBoxI range) => FlushCache?.Invoke(storage, new object[] { range, 0 });

        /// <summary>The world box of storage voxels [from, to) of the planet.</summary>
        private static BoundingBoxD World(MyVoxelBase planet, Vector3D from, Vector3D to)
        {
            var shift = (Vector3D)planet.StorageMin + planet.SizeInMetresHalf;
            return new BoundingBoxD(from - shift, to - shift).TransformFast(planet.WorldMatrix);
        }

        /// <summary>A grid (not a projection) or a character within the distance of the leaf, along each axis.</summary>
        private static bool Near(Piece piece, double distance)
        {
            var box = World(piece.Planet, piece.Min, piece.Min + piece.Size).Inflate(distance);
            var found = new List<MyEntity>();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, found);
            return found.Any(e => (e is MyCubeGrid grid && !grid.IsPreview && !grid.MarkedForClose || e is MyCharacter && !e.MarkedForClose) &&
                                  box.Intersects(e.PositionComp.WorldAABB));
        }

        /// <summary>
        /// The leaf back to the generator's terrain, on the server and the clients: the voxel hand's revert of a box. The
        /// storage's revert takes the leaves wholly inside [min, max] with max the first voxel past the leaf, so the box
        /// is the leaf and half a voxel more on either side (a voxel's floor, a voxel's ceiling less one).
        /// </summary>
        private static void Restore(Piece piece)
        {
            var planet = piece.Planet;
            Flush(planet, piece.Min, piece.Size);
            var box = World(planet, (Vector3D)piece.Min + new Vector3D(0.5), (Vector3D)piece.Min + new Vector3D(piece.Size + 0.5));
            MyVoxelGenerator.RevertShape(planet, new MyShapeBox { Boundaries = box, Transformation = MatrixD.Identity });
            if (PerformBox != null)
                MyMultiplayer.RaiseEvent(planet.RootVoxel, x => (Action<BoundingBoxD, MatrixD, byte, MyVoxelBase.OperationType>)Delegate.CreateDelegate(
                    typeof(Action<BoundingBoxD, MatrixD, byte, MyVoxelBase.OperationType>), x, PerformBox), box, MatrixD.Identity, (byte)0, MyVoxelBase.OperationType.Revert);
        }
    }
}
