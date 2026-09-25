using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The wait for the entities being made in the background, without the 15.6 ms naps.
    ///
    /// An entity made in parallel (<c>MyEntities.CreateFromObjectBuilderParallel</c>: floating ore from every
    /// hand and ship drill, dropped items, spawned grids) is initialised on a worker; before the next parallel
    /// update of the entities the game thread waits for all of that work to end
    /// (<c>MyEntities.DrainOutstandingEntityInitWork</c>). The wait is <c>SpinWait.SpinUntil</c>, whose every
    /// 20th spin is <c>Thread.Sleep(1)</c> - and Windows wakes such a sleep at the next tick of its timer,
    /// 15.6 ms later unless something raised the timer's resolution. An init of a tenth of a millisecond cost
    /// the frame 15 ms: with bots or players drilling that was a frame of 20 ms every few seconds.
    ///
    /// The same wait here only spins and yields the core (<c>Thread.Yield</c>, <c>Thread.Sleep(0)</c>), so it
    /// ends as soon as the work does. Nothing else changes: the game thread still waits for all of it.
    /// </summary>
    [PatchShim]
    public static class EntityInitDrain
    {
        private static Func<int> _outstanding;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("EntityInitDrain", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var drain = typeof(MyEntities).GetMethod("DrainOutstandingEntityInitWork", any, null, Type.EmptyTypes, null)
                        ?? throw new MissingMethodException("MyEntities.DrainOutstandingEntityInitWork");
            var counter = typeof(MyEntities).GetField("m_outstandingEntityInitWork", any)
                          ?? throw new MissingFieldException("MyEntities.m_outstandingEntityInitWork");
            if (counter.FieldType != typeof(int)) throw new InvalidOperationException("MyEntities.m_outstandingEntityInitWork is not an int");
            _outstanding = Expression.Lambda<Func<int>>(Expression.Field(null, counter)).Compile();
            c.GetPattern(drain).Prefixes.Add(typeof(EntityInitDrain).GetMethod(nameof(DrainPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static bool DrainPrefix()
        {
            WaitUntilZero(_outstanding);
            return false;
        }

        /// <summary>Returns when <paramref name="count"/> reads 0, never sleeping on the system timer.</summary>
        public static void WaitUntilZero(Func<int> count)
        {
            var spins = 0;
            while (count() != 0)
            {
                // a few short spins for work about to end, then give the core to whoever runs it
                if (spins < 10) Thread.SpinWait(20 << spins);
                else if ((spins & 1) == 0) Thread.Yield();
                else Thread.Sleep(0);
                spins++;
                Thread.MemoryBarrier();
            }
        }
    }
}
