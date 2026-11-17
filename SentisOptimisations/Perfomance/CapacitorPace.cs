using System;
using System.Reflection;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons.Guns;
using Torch.Managers.PatchManager;
using VRage.ModAPI;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// A railgun's capacitor that is not charged updates once in 100 frames, not every frame; a switched-off one not at all.
    ///
    /// The game's capacitor (MyEntityCapacitorComponent, the railguns') updates every frame while not full: its every-frame
    /// update only calls its sink's Update, which asks RequiredInputFunc again - and that answers the same (the recharge
    /// draw) all the while it charges; it changes only as the block is switched on or off, broken or mended, charged or
    /// shot. The charge itself goes in once in 100 frames (StorePower in UpdateAfterSimulation100). A switched-off
    /// capacitor never charges (it stores nothing while off), and the game left it updating every frame for good: since
    /// the game of 16.11.2026 these updates are the session's components' (MyEntityComponentUpdater), 0.1 ms a frame on
    /// the prod at rest (0.5 ms in a battle).
    ///
    /// Here a capacitor not charged updates every 100 frames only, its sink's Update first; switched off, not at all (the
    /// game's own UpdateNotChargedState, called as it is switched on, makes it update again). Each change of its state the
    /// game tells (UpdateNotChargedState: shot, switched on or off) updates the sink at once; a block broken or mended
    /// while on draws its new input by the next 100th frame (1.7 s).
    /// </summary>
    [PatchShim]
    public static class CapacitorPace
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo Sink = typeof(MyEntityCapacitorComponent).GetField("m_sink", Any);
        private static readonly FieldInfo Block = typeof(MyEntityCapacitorComponent).GetField("m_functionalBlock", Any);

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("CapacitorPace", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var type = typeof(MyEntityCapacitorComponent);
            if (Sink == null || Block == null) throw new MissingFieldException(type.Name, "m_sink or m_functionalBlock");
            var notCharged = type.GetMethod("UpdateNotChargedState", Any, null, Type.EmptyTypes, null)
                             ?? throw new MissingMethodException(type.Name, "UpdateNotChargedState");
            var added = type.GetMethod("OnAddedToContainer", Any, null, Type.EmptyTypes, null)
                        ?? throw new MissingMethodException(type.Name, "OnAddedToContainer");
            var update100 = type.GetMethod("UpdateAfterSimulation100", Any, null, Type.EmptyTypes, null)
                            ?? throw new MissingMethodException(type.Name, "UpdateAfterSimulation100");
            MethodInfo Own(string name) => typeof(CapacitorPace).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            ctx.GetPattern(notCharged).Suffixes.Add(Own(nameof(Paced)));
            ctx.GetPattern(added).Suffixes.Add(Own(nameof(Added)));
            ctx.GetPattern(update100).Prefixes.Add(Own(nameof(Update100Prefix)));
        }

        /// <summary>What the game set its updates to, paced: every 100 frames, none while off; its sink asked at once.</summary>
        private static void Paced(MyEntityCapacitorComponent __instance)
        {
            if ((__instance.NeedsUpdate & MyEntityUpdateEnum.EACH_FRAME) == 0) return;
            var block = (MyFunctionalBlock)Block.GetValue(__instance);
            __instance.NeedsUpdate = Pace(block?.Enabled);
            (Sink.GetValue(__instance) as MyResourceSinkComponent)?.Update();
        }

        // Init asks for every frame; a capacitor added full or not tells it right after (OnCharged, UpdateNotChargedState),
        // unless its block was not there yet
        private static void Added(MyEntityCapacitorComponent __instance) => Paced(__instance);

        /// <summary>The updates a capacitor not charged gets: none while its block is off, else every 100th frame.</summary>
        public static MyEntityUpdateEnum Pace(bool? enabled) => enabled == false ? MyEntityUpdateEnum.NONE : MyEntityUpdateEnum.EACH_100TH_FRAME;

        // the sink asked what it needs before the charge goes in, as its every-frame update did
        private static void Update100Prefix(MyEntityCapacitorComponent __instance) =>
            (Sink.GetValue(__instance) as MyResourceSinkComponent)?.Update();
    }
}
