using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A static grid (a base, a station) turned into a ship is written to the log: which grid, why, who asked, and from
    /// where in the code.
    ///
    /// On the prod players say their bases turn dynamic by themselves (30.10.2026). The game turns a static grid into a
    /// ship in <c>MyCubeGrid.OnConvertToDynamic</c>, by one of two roads:
    /// <list type="bullet">
    /// <item>a request (<c>OnConvertedToShipRequest(reason)</c>): from a player's client ("Convert to ship"), or from the
    /// server itself - something sets the grid's <c>TestDynamic</c> (a grid split, a block taken off, a grid copied),
    /// a frame later the grid asks itself, and the game turns it into a ship unless it still stands in voxels
    /// (<c>ShouldBeStatic</c>);</item>
    /// <item>a call of <c>OnConvertToDynamic</c> straight from a plugin or a mod.</item>
    /// </list>
    /// So the reason and the call stack are kept where <c>TestDynamic</c> is set (the stack there says what began it),
    /// the asker where the request comes in, and at the conversion the line is written with them and the stack of the
    /// conversion itself. Written always (it is a rare event, and the one looked for), a grid at most once in 5 s.
    /// </summary>
    [PatchShim]
    public static class GridConversionLog
    {
        private const int StackFrames = 24;
        private static readonly TimeSpan OncePer = TimeSpan.FromSeconds(5);

        private sealed class Asked
        {
            public int Reason;
            public string SetBy;
            public DateTime SetAt;
            public string Asker;
            public DateTime Logged;
        }

        private static readonly ConditionalWeakTable<MyCubeGrid, Asked> Grids = new ConditionalWeakTable<MyCubeGrid, Asked>();

        // Read by SentisTests: conversions written.
        public static long Written;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("GridConversionLog", ctx, c =>
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var testDynamic = typeof(MyCubeGrid).GetProperty("TestDynamic", any)?.GetSetMethod(true)
                              ?? throw new MissingMethodException("MyCubeGrid.TestDynamic's setter");
            var request = typeof(MyCubeGrid).GetMethod("OnConvertedToShipRequest", any)
                          ?? throw new MissingMethodException("MyCubeGrid.OnConvertedToShipRequest");
            var convert = typeof(MyCubeGrid).GetMethod("OnConvertToDynamic", any, null, Type.EmptyTypes, null)
                          ?? throw new MissingMethodException("MyCubeGrid.OnConvertToDynamic");
            c.GetPattern(testDynamic).Suffixes.Add(Own(nameof(TestDynamicSet)));
            c.GetPattern(request).Prefixes.Add(Own(nameof(RequestIn)));
            c.GetPattern(convert).Prefixes.Add(Own(nameof(Converting)));
        });

        private static MethodInfo Own(string name) => typeof(GridConversionLog).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // MyCubeGrid.MyTestDynamicReason (internal to the game): its numbers
        private static string ReasonName(int reason)
        {
            switch (reason)
            {
                case 0: return "NoReason";
                case 1: return "GridCopied";
                case 2: return "GridSplit";
                case 3: return "GridSplitByBlock";
                case 4: return "ConvertToShip";
                default: return reason.ToString();
            }
        }

        // a static grid told to test whether it should be a ship: what set it
        private static void TestDynamicSet(MyCubeGrid __instance, int value)
        {
            try
            {
                if (value == 0 || !__instance.IsStatic || !Sync.IsServer) return;
                var asked = Grids.GetValue(__instance, _ => new Asked());
                asked.Reason = value;
                asked.SetBy = Stack(2);
                asked.SetAt = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "GridConversionLog: TestDynamic");
            }
        }

        // the request to turn it into a ship: from a client, or the server's own
        private static void RequestIn(MyCubeGrid __instance, int reason)
        {
            try
            {
                if (!__instance.IsStatic) return;
                var asked = Grids.GetValue(__instance, _ => new Asked());
                var context = MyEventContext.Current;
                asked.Asker = context.IsLocallyInvoked || !context.Sender.IsValid
                    ? "the server itself (" + ReasonName(reason) + ")"
                    : Player(context.Sender.Value) + " (" + ReasonName(reason) + ")";
                if (asked.Reason == 0) asked.Reason = reason;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "GridConversionLog: request");
            }
        }

        private static void Converting(MyCubeGrid __instance)
        {
            try
            {
                if (!__instance.IsStatic || !Sync.IsServer) return;
                var asked = Grids.GetValue(__instance, _ => new Asked());
                var now = DateTime.UtcNow;
                if (now - asked.Logged < OncePer) return;
                asked.Logged = now;
                Written++;
                var owner = __instance.BigOwners.Count == 0 ? "nobody" : string.Join(", ", __instance.BigOwners.Select(o => MySession.Static.Players.TryGetIdentity(o)?.DisplayName ?? o.ToString()));
                var at = __instance.PositionComp.WorldAABB.Center;
                var setBy = asked.SetBy == null ? "not set" : $"{ReasonName(asked.Reason)}, {(now - asked.SetAt).TotalSeconds:0.0} s before, by {asked.SetBy}";
                SentisOptimisationsPlugin.Log.Warn($"GridConversionLog: static grid '{__instance.DisplayName}' ({__instance.EntityId}, owner {owner}, {__instance.BlocksCount} blocks, " +
                                                   $"at {at.X:0} {at.Y:0} {at.Z:0}) turns into a ship; asked by {asked.Asker ?? "nobody: called directly"}; " +
                                                   $"TestDynamic: {setBy}; converted by {Stack(2)}");
                asked.Reason = 0;
                asked.SetBy = null;
                asked.Asker = null;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Warn(e, "GridConversionLog: conversion");
            }
        }

        private static string Player(ulong steamId)
        {
            var identityId = MySession.Static.Players.TryGetIdentityId(steamId);
            var name = MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName;
            return $"player '{name ?? "?"}' ({steamId})";
        }

        /// <summary>The callers, innermost first, as "Type.Method &lt;- Type.Method", without this class's frames.</summary>
        private static string Stack(int skip)
        {
            var frames = new StackTrace(skip, false).GetFrames();
            if (frames == null) return "?";
            return string.Join(" <- ", frames.Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType != typeof(GridConversionLog))
                .Take(StackFrames).Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name));
        }
    }
}
