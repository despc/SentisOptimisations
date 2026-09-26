using System;
using System.Reflection;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.Conveyors;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A grid that closes does not take its conveyor blocks off its conveyor system's power event one by one.
    ///
    /// Every conveyor endpoint of a grid subscribes to the one resource sink of the grid's conveyor system
    /// (<c>IsPoweredChanged</c>), and unsubscribes when the block leaves the system. Removing a subscriber from an
    /// event of N copies the other N - 1, so a closing grid paid for its conveyor blocks squared: a quarter of a
    /// second for 64 production grids (dotTrace, refinery_perf cleanup), more for one big ship. When the grid itself
    /// closes, its conveyor system and that sink go with it, and nothing is left to unsubscribe from: the endpoint
    /// just lets go of it. A block ground off a grid that lives on unsubscribes as before.
    /// </summary>
    [PatchShim]
    public static class ConveyorEndpointClose
    {
        private static FieldInfo _sink;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ConveyorEndpointClose", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var unregister = typeof(MyMultilineConveyorEndpoint).GetMethod("Unregister", any, null, Type.EmptyTypes, null)
                             ?? throw new MissingMethodException("MyMultilineConveyorEndpoint.Unregister");
            _sink = typeof(MyMultilineConveyorEndpoint).GetField("m_conveyorSystemSink", any)
                    ?? throw new MissingFieldException("MyMultilineConveyorEndpoint.m_conveyorSystemSink");
            if (_sink.FieldType != typeof(MyResourceSinkComponent)) throw new InvalidOperationException("m_conveyorSystemSink is not a resource sink");
            c.GetPattern(unregister).Prefixes.Add(typeof(ConveyorEndpointClose).GetMethod(nameof(UnregisterPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        /// <summary>The grid closing: the sink is let go of instead of unsubscribed from; the rest as vanilla.</summary>
        private static void UnregisterPrefix(MyMultilineConveyorEndpoint __instance)
        {
            var grid = __instance.CubeBlock?.CubeGrid;
            if (grid != null && grid.MarkedForClose) _sink.SetValue(__instance, null);
        }
    }
}
