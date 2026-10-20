using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A store made off the game thread is put in the economy's set of players' stores on the game thread.
    ///
    /// From the stand (20.10.2026, the regression suite): the game thread froze for good in <c>HashSet.AddIfNotPresent</c>
    /// under <c>MyStoreBlock.RegisterPlayerStore</c> as a player put up an offer, and the watchdog killed the server.
    /// <c>MySessionComponentEconomy.m_playersStores</c> is a plain HashSet, and <c>MyStoreBlock.Init</c> adds a store to
    /// it when the store's builder has items. A grid made in parallel (<c>MyEntities.CreateFromObjectBuilderParallel</c>:
    /// a paste, a prefab, a restore) inits its blocks on the worker threads: stores made together write the set together,
    /// its chains are left broken (looped), and the next add or remove on the game thread walks a loop for ever. The
    /// scenario <c>player_store_race</c> (SentisTests) makes 256 stores in parallel: vanilla, the set comes out broken in
    /// the first round (254 counted, 255 in the chains, of 258).
    ///
    /// <c>Init</c>'s call of <c>RegisterPlayerStore</c> goes here: on the game thread it is made as it was, off it it is
    /// made on the game thread at the start of the next frame (a store closed by then is not added). Every other write of
    /// the set (an offer or an order put up, a store deleted) is the game thread's. The tiny methods of the set itself are
    /// inlined by the JIT, so a prefix on them never ran.
    /// </summary>
    [PatchShim]
    public static class PlayersStoreThread
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo RegisterPlayerStore = typeof(MyStoreBlock).GetMethod("RegisterPlayerStore", Any);
        private static readonly Action<MyStoreBlock> Register = Accessors.Method<MyStoreBlock, Action<MyStoreBlock>>("RegisterPlayerStore");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("PlayersStoreThread", ctx, c =>
        {
            if (RegisterPlayerStore == null || Register == null) throw new MissingMethodException("MyStoreBlock.RegisterPlayerStore");
            var init = typeof(MyStoreBlock).GetMethod("Init", Any, null, new[] { typeof(VRage.Game.MyObjectBuilder_CubeBlock), typeof(Sandbox.Game.Entities.MyCubeGrid) }, null)
                       ?? throw new MissingMethodException("MyStoreBlock.Init");
            c.GetPattern(init).Transpilers.Add(typeof(PlayersStoreThread).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand) || operand.Value != RegisterPlayerStore) continue;
                var call = new MsilInstruction(OpCodes.Call).InlineValue(typeof(PlayersStoreThread).GetMethod(nameof(RegisterFromInit), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
                found++;
            }
            if (found != 1)
            {
                SentisOptimisationsPlugin.Log.Error($"PlayersStoreThread: {found} calls of RegisterPlayerStore in MyStoreBlock.Init, not 1; left as it is");
                return instructions;
            }
            return list;
        }

        private static void RegisterFromInit(MyStoreBlock store)
        {
            if (MySandboxGame.Static == null || MySandboxGame.Static.UpdateThread == Thread.CurrentThread)
            {
                Register(store);
                return;
            }
            MySandboxGame.Static.Invoke(() =>
            {
                if (!store.Closed && !store.MarkedForClose) Register(store);
            }, "SO PlayersStoreThread");
        }
    }
}
