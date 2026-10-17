using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Whether the world is being saved, for the grid storage: a grid is not put into it during a save.
    ///
    /// The storage keeps each grid as files <c>gs-&lt;id&gt;*.sbc</c> right in the save folder, and its list of records in
    /// the world. <c>MySessionSnapshot.Save</c> (a background thread) writes the world into <c>.new</c>, copies the
    /// <c>gs-</c> files there as they are at that moment (<c>CopyGridFilesTo</c>), copies <c>.new</c> back and then
    /// deletes every file of the save folder that is not in <c>.new</c>. A grid stored in between had its files deleted,
    /// while its record stayed; on the next load the game wrote "Saved grid data ... is missing storage files and would
    /// be deleted" and the grid was gone. On the server (15-16.10.2026): "Session save cleanup removing grid storage
    /// file 'gs-...' not present in staging folder" seconds after "... was stored by ...", inside an autosave - four
    /// grids in three days.
    ///
    /// Here a <c>Save</c> holds <see cref="Saving"/> open from its start to its end; <see cref="GridStorageServerCheck"/>
    /// refuses a store request while it is open (before the game drops the grid's items or queues the request), and
    /// the player is told to try again after the save. A save that does not come back (it threw) holds it no longer
    /// than <see cref="MaxWait"/>. Fetching grids back goes on during a save: it only reads the files.
    /// </summary>
    [PatchShim]
    public static class GridStorageSaveWindow
    {
        /// <summary>The longest a save holds the storage (a save of this world takes about 25 s).</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

        internal static readonly SaveWindow Window = new SaveWindow((long)(MaxWait.TotalSeconds * Stopwatch.Frequency));

        /// <summary>Whether a save of the world is running now.</summary>
        public static bool Saving => Window.IsOpen(Stopwatch.GetTimestamp());

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridStorageSaveWindow", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var saves = typeof(MySessionSnapshot).GetMethods(instance).Where(m => m.Name == "Save" && m.ReturnType == typeof(bool)).ToList();
            if (saves.Count != 1)
                throw new MissingMethodException($"MySessionSnapshot.Save: {saves.Count} found, 1 expected");
            MethodInfo Own(string name) => typeof(GridStorageSaveWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(saves[0]).Prefixes.Add(Own(nameof(SavePrefix)));
            ctx.GetPattern(saves[0]).Suffixes.Add(Own(nameof(SaveSuffix)));
        }

        private static void SavePrefix() => Window.Open(Stopwatch.GetTimestamp());

        private static void SaveSuffix() => Window.Close();

        /// <summary>
        /// Whether a save is running: open from the start of a save to its end, the latest start counting when saves
        /// overlap; past <c>maxTicks</c> since the latest start it counts as closed (a save that threw never closes).
        /// </summary>
        public sealed class SaveWindow
        {
            private readonly object _lock = new object();
            private readonly long _maxTicks;
            private int _depth;
            private long _since;

            public SaveWindow(long maxTicks) => _maxTicks = maxTicks;

            public void Open(long now)
            {
                lock (_lock)
                {
                    if (_depth > 0 && now - _since > _maxTicks) _depth = 0;
                    _depth++;
                    _since = now;
                }
            }

            public void Close()
            {
                lock (_lock)
                {
                    if (_depth > 0) _depth--;
                }
            }

            public bool IsOpen(long now)
            {
                lock (_lock)
                {
                    return _depth > 0 && now - _since <= _maxTicks;
                }
            }
        }
    }
}
