using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.SessionComponents;
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
    /// A store request accepted before the save and come to its turn during it (the game takes one request a frame, and
    /// at a low sim speed a queue of a few dozen lasts seconds) is refused here, where the game writes the files
    /// (<c>StoreRequest.Dispatch</c>); its grid stays in the world. Fetching grids back goes on during a save: it only
    /// reads the files.
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

            // the point where the files are written: on its own, the window and the refusals of new requests work without it
            try
            {
                var request = typeof(MyGridsStorageSessionComponent).GetNestedType("StoreRequest", BindingFlags.NonPublic)
                              ?? throw new TypeLoadException("MyGridsStorageSessionComponent.StoreRequest");
                var dispatch = request.GetMethod("Dispatch", instance, null, new[] { typeof(MyGridsStorageSessionComponent) }, null)
                               ?? throw new MissingMethodException("StoreRequest.Dispatch");
                _notify = request.GetMethod("Notify", instance, null, new[] { typeof(MyGridStorageRequestResult) }, null)
                          ?? throw new MissingMethodException("StoreRequest.Notify");
                _grid = request.GetField("GridToStore", instance) ?? throw new MissingFieldException("StoreRequest.GridToStore");
                _endpoint = request.BaseType?.GetField("m_callerEndpointId", instance) ?? throw new MissingFieldException("StorageRequestBase.m_callerEndpointId");
                Harmony.Patch(dispatch, prefix: Own(nameof(DispatchPrefix)));
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Grid storage: queued store requests are not checked for a save; new ones still are");
            }
        }

        private static MethodInfo _notify;
        private static FieldInfo _grid, _endpoint;

        private static void SavePrefix() => Window.Open();

        // (no __exception: an exception of the save goes on as it is)
        private static void SaveFinalizer()
        {
            if (!Window.Close()) SentisOptimisationsPlugin.Log.Warn("Grid storage: a save ended that was not seen starting");
        }

        /// <summary>
        /// A store request accepted before a save and come to its turn during it: refused as a new one would be (its grid
        /// stays; the items were already dropped by the game when it was accepted).
        /// </summary>
        private static bool DispatchPrefix(object __instance)
        {
            if (!Saving) return true;
            try
            {
                var grid = _grid.GetValue(__instance) as MyCubeGrid;
                var sender = _endpoint.GetValue(__instance) as ulong?;
                var identityId = sender.HasValue ? MySession.Static.Players.TryGetIdentityId(sender.Value) : 0;
                var name = identityId == 0 ? null : MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName;
                SentisOptimisationsPlugin.Log.Info($"Grid storage: '{grid?.DisplayName}' ({grid?.EntityId}) not stored for '{name}' ({sender}): the world is being saved (queued)");
                _notify.Invoke(__instance, new object[] { MyGridStorageRequestResult.UnspecifiedStorageIssue });
                if (identityId != 0) GridStorageServerCheck.TellSaving(identityId);
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "Grid storage: a queued store request during a save could not be answered; it is dropped");
            }
            return false;
        }

        /// <summary>Whether a save is running: open from the start of a save to its end, until the last of overlapping saves ends.</summary>
        public sealed class SaveWindow
        {
            private readonly object _lock = new object();
            private int _depth;

            public void Open()
            {
                lock (_lock) _depth++;
            }

            /// <summary>Ends a save; false when no save was open (the count stays at 0).</summary>
            public bool Close()
            {
                lock (_lock)
                {
                    if (_depth == 0) return false;
                    _depth--;
                    return true;
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
