using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// One subscription to "a client left" for all terminal blocks, instead of one per block.
    ///
    /// Every terminal block of the world - refinery, container, conveyor sorter, light - subscribes on the server
    /// to <c>MyClientCollection.ClientRemoved</c> when it is made and unsubscribes when it closes, so that a text
    /// editor a leaving player had open on it is closed. An event with N subscribers is one delegate over an array
    /// of N; adding or removing one copies the whole array. With tens of thousands of terminal blocks in a world,
    /// every block that closes (a grid deleted, a ship ground down, a cleanup) and every block that is made (a
    /// paste, a grid streamed in) copies tens of thousands of entries: deleting 64 production grids took the
    /// frame 1 s of that alone (dotTrace, refinery_perf cleanup).
    ///
    /// Here the blocks do not subscribe; a single handler does, and the blocks that have an editor open (the only
    /// ones the vanilla handler acts on) are kept in a small list as the editor opens and closes. When a client
    /// leaves, those of its blocks get the vanilla handler, the same call as before.
    /// </summary>
    [PatchShim]
    public static class TerminalBlockClientRemoved
    {
        private static MethodInfo _add, _remove;
        private static Action<MyTerminalBlock, ulong> _handler;

        // game thread: the network events that open and close editors, the closing of blocks and the leaving of
        // clients are all handled there
        private static readonly Dictionary<MyTerminalBlock, ulong> Open = new Dictionary<MyTerminalBlock, ulong>();
        private static MyClientCollection _subscribed;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("TerminalBlockClientRemoved", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var clientRemoved = typeof(MyClientCollection).GetEvent("ClientRemoved", any) ?? throw new MissingMemberException("MyClientCollection.ClientRemoved");
            _add = clientRemoved.GetAddMethod(true);
            _remove = clientRemoved.GetRemoveMethod(true);
            var handler = typeof(MyTerminalBlock).GetMethod("ClientRemoved", any, null, new[] { typeof(ulong) }, null)
                          ?? throw new MissingMethodException("MyTerminalBlock.ClientRemoved");
            _handler = (Action<MyTerminalBlock, ulong>)Delegate.CreateDelegate(typeof(Action<MyTerminalBlock, ulong>), handler);

            var init = typeof(MyTerminalBlock).GetMethods(any).Single(m => m.Name == "Init" && m.DeclaringType == typeof(MyTerminalBlock) && m.GetParameters().Length == 2);
            var closing = typeof(MyTerminalBlock).GetMethod("Closing", any | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                          ?? throw new MissingMethodException("MyTerminalBlock.Closing");
            var onChangeOpen = typeof(MyTerminalBlock).GetMethod("OnChangeOpen", any, null, new[] { typeof(bool), typeof(bool), typeof(ulong) }, null)
                               ?? throw new MissingMethodException("MyTerminalBlock.OnChangeOpen");
            MethodInfo Own(string name) => typeof(TerminalBlockClientRemoved).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

            ctx.GetPattern(init).Transpilers.Add(Own(nameof(InitTranspiler)));
            ctx.GetPattern(closing).Transpilers.Add(Own(nameof(ClosingTranspiler)));
            ctx.GetPattern(closing).Suffixes.Add(Own(nameof(ClosingSuffix)));
            ctx.GetPattern(onChangeOpen).Suffixes.Add(Own(nameof(OnChangeOpenSuffix)));
        }

        private static IEnumerable<MsilInstruction> InitTranspiler(IEnumerable<MsilInstruction> instructions) =>
            Replace(instructions, _add, typeof(TerminalBlockClientRemoved).GetMethod(nameof(Subscribe), BindingFlags.Static | BindingFlags.Public), "Init");

        private static IEnumerable<MsilInstruction> ClosingTranspiler(IEnumerable<MsilInstruction> instructions) =>
            Replace(instructions, _remove, typeof(TerminalBlockClientRemoved).GetMethod(nameof(Unsubscribe), BindingFlags.Static | BindingFlags.Public), "Closing");

        /// <summary>The one call to the event's accessor becomes a call to <paramref name="replacement"/> (same stack).</summary>
        private static IEnumerable<MsilInstruction> Replace(IEnumerable<MsilInstruction> instructions, MethodInfo accessor, MethodInfo replacement, string where)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand) || operand.Value != accessor) continue;
                var call = new MsilInstruction(OpCodes.Call).InlineValue(replacement);
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
                found++;
            }
            if (found != 1) throw new InvalidOperationException($"TerminalBlockClientRemoved: MyTerminalBlock.{where} has {found} ClientRemoved accessors");
            return list;
        }

        /// <summary>In place of a block's own subscription: the one shared subscription, made once per session.</summary>
        public static void Subscribe(MyClientCollection clients, Action<ulong> handler)
        {
            if (clients == null || ReferenceEquals(clients, _subscribed)) return;
            if (_subscribed != null) _remove.Invoke(_subscribed, new object[] { (Action<ulong>)OnClientRemoved });
            _add.Invoke(clients, new object[] { (Action<ulong>)OnClientRemoved });
            _subscribed = clients;
        }

        /// <summary>In place of a block's own unsubscription: nothing to remove.</summary>
        public static void Unsubscribe(MyClientCollection clients, Action<ulong> handler)
        {
        }

        private static void OnChangeOpenSuffix(MyTerminalBlock __instance, bool isOpen, ulong user)
        {
            if (isOpen && user != 0) Open[__instance] = user;
            else Open.Remove(__instance);
        }

        private static void ClosingSuffix(MyTerminalBlock __instance) => Open.Remove(__instance);

        /// <summary>A client left: the blocks it had an editor open on get the vanilla handler.</summary>
        private static void OnClientRemoved(ulong steamId)
        {
            if (Open.Count == 0) return;
            foreach (var pair in Open.Where(p => p.Value == steamId).ToList())
            {
                Open.Remove(pair.Key);
                if (!pair.Key.Closed) _handler(pair.Key, steamId);
            }
        }
    }
}
