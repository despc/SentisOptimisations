using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.Network;
using VRageMath;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// The head of a rotor, a hinge or an advanced rotor checked after it is placed, and put right.
    ///
    /// Reported from the server (11.10.2026): the head of an advanced rotor placed as a frame falls on its side and jerks,
    /// throws a player standing by, once blew up; it stays so after a reconnect; not without SentisOptimisations. The
    /// stand did not show it (rotor_place, rotor_spawn in SentisTests), so the head is checked where it is made: for
    /// <see cref="ChecksAfterPlacing"/> passes, <see cref="FramesBetween"/> frames apart, after the game made it
    /// (<c>OnBuildSuccess</c> of a placed stator, <c>DoRecreateTop</c> of "Add head" in the terminal). A head that is not
    /// where the stator puts it - off its place by more than <see cref="MaxOffsetM"/>, its axis off the stator's by more
    /// than <see cref="MaxTiltDegrees"/>, not attached, without its constraint in the physics world - is detached, put
    /// where the game puts a new head (<c>GetTopGridMatrix</c> and the definition's offset, as <c>CreateTopPart</c>),
    /// stopped, and attached again; then the stator's synced properties go to every client again, the connection among
    /// them, so a client that missed it attaches the head too. A head turned about the stator's axis is not touched: the
    /// hinge turns it.
    ///
    /// Switched on and off in the plugin's settings (<c>MechanicalTopCheck</c>) at any time; every head put right is
    /// written to the log.
    /// </summary>
    [PatchShim]
    public static class MechanicalTopCheck
    {
        public const int ChecksAfterPlacing = 12;
        public const int FramesBetween = 15;
        public const double MaxOffsetM = 0.2;
        public const double MaxTiltDegrees = 3;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo TopGridMatrix = typeof(MyMechanicalConnectionBlockBase).GetMethod("GetTopGridMatrix", Any);
        private static readonly PropertyInfo Constraint = typeof(MyMechanicalConnectionBlockBase).GetProperty("Constraint", Any);
        private static readonly FieldInfo ConnectionState = typeof(MyMechanicalConnectionBlockBase).GetField("m_connectionState", Any);
        private static readonly PropertyInfo Welded = typeof(MyMechanicalConnectionBlockBase).GetProperty("m_welded", Any);
        private static readonly MethodInfo DetachGroups = Array.Find(typeof(MyMechanicalConnectionBlockBase).GetMethods(Any),
            m => m.Name == "Detach" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));

        private sealed class Watch
        {
            public MyMechanicalConnectionBlockBase Stator;
            /// <summary>The head the game made for it: still the stator's when it came off.</summary>
            public long HeadId;
            public int Left;
            public ulong Next;
        }

        private static readonly Dictionary<long, Watch> Watched = new Dictionary<long, Watch>();
        private static readonly List<long> Done = new List<long>();

        /// <summary>Heads put right since the start, for the statistics and the tests.</summary>
        public static long PutRight;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("MechanicalTopCheck", ctx, c =>
        {
            if (TopGridMatrix == null || Constraint == null || Welded == null || DetachGroups == null || ConnectionState == null)
                throw new MissingMemberException("MyMechanicalConnectionBlockBase: GetTopGridMatrix, Constraint, m_welded or Detach(bool)");
            var type = typeof(MyMechanicalConnectionBlockBase);
            var built = type.GetMethod(nameof(MyMechanicalConnectionBlockBase.OnBuildSuccess), Any)
                        ?? throw new MissingMethodException("MyMechanicalConnectionBlockBase.OnBuildSuccess");
            var recreate = type.GetMethod("DoRecreateTop", Any) ?? throw new MissingMethodException("MyMechanicalConnectionBlockBase.DoRecreateTop");
            var suffix = typeof(MechanicalTopCheck).GetMethod(nameof(MadeSuffix), BindingFlags.Static | BindingFlags.NonPublic);
            c.GetPattern(built).Suffixes.Add(suffix);
            c.GetPattern(recreate).Suffixes.Add(suffix);
            // and every override: a piston's (MyPistonBase.OnBuildSuccess loads its subparts, then calls the base) did not
            // reach the base's suffix
            foreach (var derived in type.Assembly.GetTypes())
            {
                if (!type.IsAssignableFrom(derived) || derived == type) continue;
                var own = derived.GetMethod(nameof(MyMechanicalConnectionBlockBase.OnBuildSuccess), Any | BindingFlags.DeclaredOnly);
                if (own != null) c.GetPattern(own).Suffixes.Add(suffix);
            }
        });

        private static bool On => SentisOptimisationsPlugin.Config?.MechanicalTopCheck == true;

        private static void MadeSuffix(MyMechanicalConnectionBlockBase __instance)
        {
            if (!On || !Sync.IsServer || __instance == null) return;
            Watched[__instance.EntityId] = new Watch
            {
                Stator = __instance, Left = ChecksAfterPlacing, HeadId = HeadOf(__instance, 0)?.EntityId ?? 0,
                Next = (MySandboxGame.Static?.SimulationFrameCounter ?? 0) + FramesBetween,
            };
        }

        /// <summary>Every frame, game thread.</summary>
        public static void Tick()
        {
            if (Watched.Count == 0) return;
            if (!On)
            {
                Watched.Clear();
                return;
            }
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            foreach (var pair in Watched)
            {
                var w = pair.Value;
                if (w.Stator.MarkedForClose || w.Stator.Closed)
                {
                    Done.Add(pair.Key);
                    continue;
                }
                // the head learnt as soon as there is one: a piston attaches its head a frame or two after it is made
                if (w.HeadId == 0) w.HeadId = HeadOf(w.Stator, 0)?.EntityId ?? 0;
                if (frame < w.Next) continue;
                w.Next = frame + FramesBetween;
                if (--w.Left < 0)
                {
                    Done.Add(pair.Key);
                    continue;
                }
                try
                {
                    if (w.HeadId == 0) w.HeadId = HeadOf(w.Stator, 0)?.EntityId ?? 0;
                    if (Check(w.Stator, w.HeadId)) Done.Add(pair.Key);
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error(e, "MechanicalTopCheck " + w.Stator.EntityId);
                    Done.Add(pair.Key);
                }
            }
            foreach (var id in Done) Watched.Remove(id);
            Done.Clear();
        }

        /// <summary>Where the game puts a new head of this stator: what CreateTopPart does with GetTopGridMatrix.</summary>
        public static MatrixD Expected(MyMechanicalConnectionBlockBase stator, MyAttachableTopBlockBase top)
        {
            var matrix = (MatrixD)TopGridMatrix.Invoke(stator, null);
            var definition = top.BlockDefinition;
            if (definition.MechanicalTopInitialPlacementOffset.HasValue)
                matrix.Translation = Vector3D.Transform(definition.MechanicalTopInitialPlacementOffset.Value, matrix);
            else if (definition.Center != Vector3.Zero)
                matrix.Translation = Vector3D.Transform(-definition.Center * MyDefinitionManager.Static.GetCubeSize(definition.CubeSize), matrix);
            return matrix;
        }

        /// <summary>
        /// The stator's head: the attached one, or the one its synced connection names - a head that came off and
        /// was not let go is still this stator's.
        /// </summary>
        public static MyAttachableTopBlockBase HeadOf(MyMechanicalConnectionBlockBase stator, long madeId)
        {
            if (stator.TopBlock != null) return stator.TopBlock;
            // the head made for it, come off and not taken by another stator
            if (madeId != 0 && MyEntities.TryGetEntityById(madeId, out MyAttachableTopBlockBase made) && !made.MarkedForClose && (made.Stator == null || made.Stator == stator)) return made;
            var sync = ConnectionState.GetValue(stator);
            var state = sync?.GetType().GetProperty("Value")?.GetValue(sync);
            var id = state?.GetType().GetField("TopBlockId")?.GetValue(state) as long?;
            return id.HasValue && id.Value != 0 && MyEntities.TryGetEntityById(id.Value, out MyAttachableTopBlockBase top) && !top.MarkedForClose ? top : null;
        }

        /// <summary>What is wrong with the head, or null.</summary>
        public static string Problem(MyMechanicalConnectionBlockBase stator, long madeId, out MatrixD expected)
        {
            expected = MatrixD.Identity;
            var top = HeadOf(stator, madeId);
            if (top == null || top.CubeGrid?.Physics == null) return null;
            // a head of its own grid, where the game made it: a head welded into something bigger is not ours to move
            if (top.CubeGrid.BlocksCount != 1 || top.Position != Vector3I.Zero) return null;
            expected = Expected(stator, top);
            var actual = top.CubeGrid.WorldMatrix;
            var offset = (actual.Translation - expected.Translation).Length();
            var tilt = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(actual.Up, expected.Up), -1, 1)));
            var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)stator).IsAttached;
            var welded = (bool)Welded.GetValue(stator);
            var hinge = Constraint.GetValue(stator) as HkConstraint;
            var held = welded || hinge != null && hinge.InWorld;
            if (offset <= MaxOffsetM && tilt <= MaxTiltDegrees && attached && held) return null;
            return $"off its place by {offset:0.00} m, its axis off the stator's by {tilt:0.0}°, {(attached ? "attached" : "not attached")}, " +
                   $"{(welded ? "welded" : hinge == null ? "no constraint" : hinge.InWorld ? "constraint in the world" : "constraint out of the world")}";
        }

        /// <summary>Puts the head right when it is wrong; true when the stator needs no more checks.</summary>
        private static bool Check(MyMechanicalConnectionBlockBase stator, long madeId)
        {
            var problem = Problem(stator, madeId, out var expected);
            if (problem == null) return false;
            var top = HeadOf(stator, madeId);
            var head = top.CubeGrid;
            if (stator.TopBlock != null) DetachGroups.Invoke(stator, new object[] { true });
            if (head.Physics != null)
            {
                head.Physics.LinearVelocity = stator.CubeGrid.Physics?.LinearVelocity ?? Vector3.Zero;
                head.Physics.AngularVelocity = Vector3.Zero;
            }
            ((IMyEntity)head).SetWorldMatrix(expected);
            ((Sandbox.ModAPI.IMyMechanicalConnectionBlock)stator).Attach((Sandbox.ModAPI.IMyAttachableTopBlock)top, true);
            ResendProperties(stator);
            PutRight++;
            SentisOptimisationsPlugin.Log.Warn($"MechanicalTopCheck: the head of {stator.BlockDefinition.Id.SubtypeName} '{stator.CustomName}' on {stator.CubeGrid.DisplayName} " +
                                               $"({stator.EntityId}) was {problem}; put where the stator puts it and attached again");
            return false;     // checked again: attaching takes the next frame
        }

        private static readonly FieldInfo ReplicableGroups = typeof(MyReplicationServer).GetField("m_replicableGroups", Any);
        private static readonly Type PropertyGroupType = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Replication.StateGroups.MyPropertySyncStateGroup");

        /// <summary>
        /// Every synced property of the stator - its connection among them - sent to every client again, as if they
        /// had all just changed: a client that missed the connection gets it.
        /// </summary>
        public static void ResendProperties(MyMechanicalConnectionBlockBase stator)
        {
            try
            {
                if (!(MyMultiplayer.Static?.ReplicationLayer is MyReplicationServer server) || ReplicableGroups == null || PropertyGroupType == null) return;
                var replicable = MyExternalReplicable.FindByObject(stator);
                if (replicable == null) return;
                if (!(ReplicableGroups.GetValue(server) is IDictionary groups) || !(groups[replicable] is IEnumerable list)) return;
                var serverData = PropertyGroupType.GetField("m_serverData", Any);
                foreach (var group in list)
                {
                    if (group == null || !PropertyGroupType.IsInstanceOfType(group)) continue;
                    var data = serverData?.GetValue(group);
                    var perClient = data?.GetType().GetField("ServerClientData")?.GetValue(data) as IDictionary;
                    if (perClient == null) continue;
                    foreach (var clientData in perClient.Values)
                    {
                        var dirty = clientData.GetType().GetField("DirtyProperties");
                        dirty.SetValue(clientData, new VRage.Library.Collections.SmallBitField(true));
                    }
                    server.AddToDirtyGroups((IMyStateGroup)group);
                }
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.Log.Error(e, "MechanicalTopCheck: resending the stator's properties");
            }
        }
    }
}
