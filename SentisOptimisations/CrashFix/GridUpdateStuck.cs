using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A grid whose update threw is not left "being dispatched" for good.
    ///
    /// <c>MyCubeGrid.Dispatch</c> marks the queue it runs (<c>BeginUpdate</c>: <c>m_updateInProgress</c>) and unmarks it at
    /// its end (<c>EndUpdate</c>), with no finally between: an update that throws leaves the mark. In the game the throw
    /// takes the server down; here SentisOptimisations lets the entities' pass go on (CrashFixPatch's dispatch finalizer),
    /// and the grid stayed marked - every frame after, each of its dispatches threw "An update queue is already being
    /// dispatched for this entity" out of the parallel pass, and the pass stopped there: 1200 a second of them and the
    /// server's entities half updated, for as long as it ran (prod 06.11.2026 23:12, after a NullReferenceException in a
    /// ship's thrust, MyThrusterBlockThrustComponent.CalculateMass; the players: "it froze").
    ///
    /// The frame each grid's dispatch began in is kept. A grid found marked in a later frame - no frame's dispatch runs
    /// on into the next - or marked for another queue - a grid's queues are dispatched one after another, never one in
    /// another - is unmarked (<c>EndUpdate</c>, as the dispatch that threw would have done) and its update goes on; each
    /// one is written to the log. Marked for the same queue within the same frame it is that queue dispatched twice at
    /// once, and throws as in the game.
    /// </summary>
    [PatchShim]
    public static class GridUpdateStuck
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Func<MyCubeGrid, MyCubeGrid.UpdateQueue> InProgress = Accessors.Field<MyCubeGrid, MyCubeGrid.UpdateQueue>("m_updateInProgress");
        private static readonly Action<MyCubeGrid> EndUpdate = BuildEndUpdate();
        private static readonly ConditionalWeakTable<MyCubeGrid, StrongBox<int>> BeganAt = new ConditionalWeakTable<MyCubeGrid, StrongBox<int>>();
        private static int _freed;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridUpdateStuck", ctx, c =>
        {
            if (EndUpdate == null) throw new MissingMethodException("MyCubeGrid.EndUpdate");
            var begin = typeof(MyCubeGrid).GetMethod("BeginUpdate", Any, null, new[] { typeof(MyCubeGrid.UpdateQueue) }, null)
                        ?? throw new MissingMethodException("MyCubeGrid.BeginUpdate");
            c.GetPattern(begin).Prefixes.Add(typeof(GridUpdateStuck).GetMethod(nameof(BeginPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static Action<MyCubeGrid> BuildEndUpdate()
        {
            var method = typeof(MyCubeGrid).GetMethod("EndUpdate", Any, null, Type.EmptyTypes, null);
            return method == null ? null : (Action<MyCubeGrid>)Delegate.CreateDelegate(typeof(Action<MyCubeGrid>), method, false);
        }

        private static void BeginPrefix(MyCubeGrid __instance, MyCubeGrid.UpdateQueue queue)
        {
            try
            {
                var frame = MySession.Static?.GameplayFrameCounter ?? 0;
                var began = BeganAt.GetValue(__instance, _ => new StrongBox<int>());
                var marked = InProgress(__instance);
                if (marked != MyCubeGrid.UpdateQueue.Invalid && (began.Value != frame || marked != queue))
                {
                    // a dispatch of an earlier frame that threw half way: what it would have done at its end
                    EndUpdate(__instance);
                    var freed = Interlocked.Increment(ref _freed);
                    if (freed <= 20 || freed % 100 == 0)
                        SentisOptimisationsPlugin.Log.Warn($"GridUpdateStuck: grid '{__instance.DisplayName}' ({__instance.EntityId}) was left dispatching its {marked} " +
                                                           $"since frame {began.Value} (an update of it threw); unmarked at frame {frame}, its {queue} goes on ({freed} so far)");
                }
                began.Value = frame;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "GridUpdateStuck failed");
            }
        }
    }
}
