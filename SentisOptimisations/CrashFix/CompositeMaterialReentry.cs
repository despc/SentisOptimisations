using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// The material of a predefined asteroid read inside the reading of another's does not crash the server.
    ///
    /// <c>MyCompositeShapeProviderBase.ReadMaterialRange</c> collects the deposits a range touches into a list that is one
    /// for the thread (<c>[ThreadStatic] m_overlappedDeposits</c>), walks it, and clears it at the end. A predefined
    /// asteroid (<c>MyPredefinedDataProvider</c>, e.g. <c>Asteroid128-001</c>) takes the material of a deposit from its
    /// template, and the template has a composite provider of its own: on a miss of the template's cache its
    /// ReadMaterialRange runs on the same thread, inside the walk, adds to that list and clears it. The walk throws
    /// "Collection was modified" - in the voxel physics' parallel tasks, the trash's voxel reversion: the server gone
    /// (stand 22.10, 23.10 and 24.10.2026, dump crash_20261023_223544.dmp).
    ///
    /// Here a nested call on the same thread gets a list of its own, and the outer one gets its list back after it. (The
    /// nesting is common - the stand counts hundreds of thousands a minute through GetMaterialForPosition with the outer
    /// list holding its one deposit - yet the vanilla game throws only now and then; why most nested readings do not
    /// break the walk is not known. A nested reading that broke it threw, so the fix changes no material a client sees.)
    /// </summary>
    [PatchShim]
    public static class CompositeMaterialReentry
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Provider = typeof(Sandbox.Game.Entities.MyVoxelBase).Assembly.GetType("Sandbox.Game.World.Generator.MyCompositeShapeProviderBase");
        private static readonly FieldInfo Deposits = Provider?.GetField("m_overlappedDeposits", Any);

        [ThreadStatic] private static int _depth;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("CompositeMaterialReentry", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var method = Provider?.GetMethod("ReadMaterialRange", Any)
                         ?? throw new MissingMethodException("MyCompositeShapeProviderBase", "ReadMaterialRange");
            if (Deposits == null) throw new MissingFieldException("MyCompositeShapeProviderBase", "m_overlappedDeposits");
            CrashFixPatch.harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(CompositeMaterialReentry).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(CompositeMaterialReentry).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void Prefix(out object __state)
        {
            __state = null;
            if (_depth++ == 0) return;
            // nested: the outer call's list is put aside, this one makes its own
            __state = Deposits.GetValue(null);
            Deposits.SetValue(null, null);
            System.Threading.Interlocked.Increment(ref Nested);
        }

        private static Exception Finalizer(Exception __exception, object __state)
        {
            _depth--;
            if (__state != null) Deposits.SetValue(null, __state);
            return __exception;
        }

        /// <summary>Nested readings seen since the start.</summary>
        public static long Nested;
    }
}
