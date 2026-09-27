using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Torch's window told about factions without the game waiting for it.
    ///
    /// The entity tree of the Torch window (<c>Torch.Server.ViewModels.EntityTreeViewModel</c>) handles a faction change
    /// and a new faction with <c>Dispatcher.Invoke</c>: the game thread stops until the window's thread is free and has
    /// rebuilt the faction's member list - however long whatever the window is doing takes. A wild animal joins its
    /// faction (SPID) when it spawns: the same join was 0.6 ms one time and 30 ms another on the stand, the rest of the
    /// 30 ms waiting for the window (the faction itself holds only the animals alive). Here
    /// the same work is handed to the window with <c>BeginInvoke</c>, and a faction's member list asked for again before
    /// it was rebuilt is rebuilt once. Without the window (Torch with no GUI) nothing of it runs.
    /// </summary>
    [PatchShim]
    public static class TorchUiAsync
    {
        private static PropertyInfo _dispatcher, _factions;
        private static Type _factionViewModel;
        private static readonly HashSet<long> Rebuilding = new HashSet<long>();

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TorchUiAsync", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var tree = Type.GetType("Torch.Server.ViewModels.EntityTreeViewModel, Torch.Server", false);
            if (tree == null) return;   // no Torch window in this process
            _dispatcher = tree.GetProperty("ControlDispatcher", any) ?? throw new MissingMemberException("EntityTreeViewModel.ControlDispatcher");
            _factions = tree.GetProperty("Factions", any) ?? throw new MissingMemberException("EntityTreeViewModel.Factions");
            _factionViewModel = Type.GetType("Torch.Server.ViewModels.Entities.FactionViewModel, Torch.Server", true);
            MethodInfo Own(string name) => typeof(TorchUiAsync).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(tree.GetMethod("FactionChanged", any) ?? throw new MissingMethodException("EntityTreeViewModel.FactionChanged"))
                .Prefixes.Add(Own(nameof(FactionChangedPrefix)));
            ctx.GetPattern(tree.GetMethod("NewFactionCreated", any) ?? throw new MissingMethodException("EntityTreeViewModel.NewFactionCreated"))
                .Prefixes.Add(Own(nameof(NewFactionCreatedPrefix)));
        }

        /// <summary>The window's thread does it later; false when there is no window to hand it to.</summary>
        private static bool Later(object tree, Action work)
        {
            var dispatcher = _dispatcher.GetValue(tree);
            if (dispatcher == null) return false;
            var beginInvoke = dispatcher.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate), typeof(object[]) });
            if (beginInvoke == null) return false;
            beginInvoke.Invoke(dispatcher, new object[] { work, new object[0] });
            return true;
        }

        private static bool FactionChangedPrefix(object __instance, MyFactionStateChange reason, long FactionId)
        {
            try
            {
                var factions = _factions.GetValue(__instance);
                var type = factions.GetType();
                switch (reason)
                {
                    case MyFactionStateChange.RemoveFaction:
                        return !Later(__instance, () => type.GetMethod("Remove", new[] { typeof(long) })?.Invoke(factions, new object[] { FactionId }));
                    case MyFactionStateChange.FactionMemberAcceptJoin:
                    case MyFactionStateChange.FactionMemberKick:
                    case MyFactionStateChange.FactionMemberPromote:
                    case MyFactionStateChange.FactionMemberDemote:
                    case MyFactionStateChange.FactionMemberLeave:
                        lock (Rebuilding)
                            if (!Rebuilding.Add(FactionId)) return false;   // a rebuild of this faction is on its way already
                        return !Later(__instance, () =>
                        {
                            lock (Rebuilding) Rebuilding.Remove(FactionId);
                            var args = new object[] { FactionId, null };
                            if ((bool)type.GetMethod("TryGetValue").Invoke(factions, args) && args[1] != null)
                                args[1].GetType().GetMethod("GenerateMembers")?.Invoke(args[1], null);
                        });
                    default:
                        return false;   // the window does nothing for the others either
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "TorchUiAsync: the faction change goes to the window the usual way");
                lock (Rebuilding) Rebuilding.Remove(FactionId);
                return true;
            }
        }

        private static bool NewFactionCreatedPrefix(object __instance, long id)
        {
            try
            {
                var factions = _factions.GetValue(__instance);
                return !Later(__instance, () =>
                {
                    var faction = MySession.Static?.Factions.GetPlayerFaction(id);
                    if (faction == null) return;
                    var model = Activator.CreateInstance(_factionViewModel, faction);
                    var pair = Activator.CreateInstance(typeof(KeyValuePair<,>).MakeGenericType(typeof(long), _factionViewModel), faction.FactionId, model);
                    factions.GetType().GetMethod("Add", new[] { pair.GetType() })?.Invoke(factions, new[] { pair });
                });
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "TorchUiAsync: the new faction goes to the window the usual way");
                return true;
            }
        }
    }
}
