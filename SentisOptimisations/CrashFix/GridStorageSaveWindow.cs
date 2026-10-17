using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
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
    /// Here a <c>Save</c> holds <see cref="Saving"/> open from its start to its end, however it ends (a finalizer: a
    /// save that throws closes it too); <see cref="GridStorageServerCheck"/> refuses to store a grid while it is open.
    /// Fetching grids back goes on during a save: it only reads the files.
    ///
    /// A store request accepted just before a save starts is not covered: the game writes its files a frame or so later,
    /// while the save copies them only after writing the world, seconds later - it would take a queue of hundreds of
    /// requests for one to be written that late.
    /// </summary>
    [PatchShim]
    public static class GridStorageSaveWindow
    {
        internal static readonly SaveWindow Window = new SaveWindow();

        // by Harmony, not Torch: a Torch suffix does not run when the method throws, and a save that throws has to close
        // the window too
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.GridStorageSaveWindow");

        /// <summary>Whether a save of the world is running now.</summary>
        public static bool Saving => Window.IsOpen;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridStorageSaveWindow", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var saves = typeof(MySessionSnapshot).GetMethods(instance).Where(m => m.Name == "Save" && m.ReturnType == typeof(bool)).ToList();
            if (saves.Count != 1)
                throw new MissingMethodException($"MySessionSnapshot.Save: {saves.Count} found, 1 expected");
            HarmonyMethod Own(string name) => new HarmonyMethod(typeof(GridStorageSaveWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
            Harmony.Patch(saves[0], prefix: Own(nameof(SavePrefix)), finalizer: Own(nameof(SaveFinalizer)));
        }

        private static void SavePrefix() => Window.Open();

        // (no __exception: an exception of the save goes on as it is)
        private static void SaveFinalizer() => Window.Close();

        /// <summary>Whether a save is running: open from the start of a save to its end, until the last of overlapping saves ends.</summary>
        public sealed class SaveWindow
        {
            private readonly object _lock = new object();
            private int _depth;

            public void Open()
            {
                lock (_lock) _depth++;
            }

            public void Close()
            {
                lock (_lock)
                {
                    if (_depth > 0) _depth--;
                }
            }

            public bool IsOpen
            {
                get
                {
                    lock (_lock) return _depth > 0;
                }
            }
        }
    }
}
