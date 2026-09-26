using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The block limits of the online players looked through for changes without copying them.
    ///
    /// Every frame, for every online player, <c>MyPlayerCollection.SendDirtyBlockLimit</c> walks the player's limits per
    /// block type and per grid for the ones changed since. Both are <c>ConcurrentDictionary</c>s walked through their
    /// <c>Values</c> - every lock of the dictionary taken and all its entries copied into a new list and array, every
    /// frame, for each player: arrays of grid limits were one of the commonest garbage on the server (heap dump), and
    /// the locks are fought over with the threads that build and grind.
    ///
    /// Here <c>Values</c> in that method is a view that walks the dictionary as it is (the dictionary's own enumerator:
    /// no locks, no copy). The walk only looks for entries marked dirty, as before; an entry added during it is sent
    /// on this frame or the next.
    /// </summary>
    [PatchShim]
    public static class BlockLimitsWalk
    {
        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("BlockLimitsWalk", ctx, PatchImpl);

        private static MethodInfo _typeValues, _gridValues;

        internal static void PatchImpl(PatchContext ctx)
        {
            var send = typeof(MyPlayerCollection).GetMethod("SendDirtyBlockLimit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException("MyPlayerCollection.SendDirtyBlockLimit");
            _typeValues = typeof(ConcurrentDictionary<string, MyBlockLimits.MyTypeLimitData>).GetProperty("Values").GetGetMethod();
            _gridValues = typeof(ConcurrentDictionary<long, MyBlockLimits.MyGridLimitData>).GetProperty("Values").GetGetMethod();
            ctx.GetPattern(send).Transpilers.Add(typeof(BlockLimitsWalk).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            var found = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand)) continue;
                string replacement;
                if (operand.Value == _typeValues) replacement = nameof(TypeValues);
                else if (operand.Value == _gridValues) replacement = nameof(GridValues);
                else continue;
                var call = new MsilInstruction(OpCodes.Call).InlineValue(typeof(BlockLimitsWalk).GetMethod(replacement, BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var label in list[i].Labels) call.Labels.Add(label);
                list[i] = call;
                found++;
            }
            if (found != 2)
            {
                SentisOptimisationsPlugin.Log.Error($"BlockLimitsWalk: {found} Values in SendDirtyBlockLimit, not 2; left as it is");
                return instructions;
            }
            return list;
        }

        private static ICollection<MyBlockLimits.MyTypeLimitData> TypeValues(ConcurrentDictionary<string, MyBlockLimits.MyTypeLimitData> dictionary) =>
            new LiveValues<string, MyBlockLimits.MyTypeLimitData>(dictionary);

        private static ICollection<MyBlockLimits.MyGridLimitData> GridValues(ConcurrentDictionary<long, MyBlockLimits.MyGridLimitData> dictionary) =>
            new LiveValues<long, MyBlockLimits.MyGridLimitData>(dictionary);

        /// <summary>The values of a concurrent dictionary as they are, for walking them once.</summary>
        private sealed class LiveValues<TKey, TValue> : ICollection<TValue>
        {
            private readonly ConcurrentDictionary<TKey, TValue> _dictionary;
            public LiveValues(ConcurrentDictionary<TKey, TValue> dictionary) => _dictionary = dictionary;

            public IEnumerator<TValue> GetEnumerator()
            {
                foreach (var pair in _dictionary) yield return pair.Value;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public int Count => _dictionary.Count;
            public bool IsReadOnly => true;
            public bool Contains(TValue item) => _dictionary.Values.Contains(item);
            public void CopyTo(TValue[] array, int arrayIndex) => _dictionary.Values.CopyTo(array, arrayIndex);
            public void Add(TValue item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Remove(TValue item) => throw new NotSupportedException();
        }
    }
}
