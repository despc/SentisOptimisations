using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// No walking particles queued on the server for every touch of a character with the ground.
    ///
    /// <c>MyCharacter.RigidBody_ContactPointCallback</c> runs for every contact point of a character's body, and for
    /// one with voxels it queues a call to the game thread that spawns dust under the feet. A dedicated server draws
    /// nothing, yet each contact made a closure over the contact (the compiler makes it at the top of the method, for
    /// every contact, voxels or not), a delegate over it and an entry in the invoke queue: a quarter of a million of
    /// each between two young collections with a few bots walking about (heap dump), a steady share of the garbage
    /// whose collections are the long frames.
    ///
    /// The same callback also read the voxel material under the foot for the footprints, which a dedicated server
    /// drops at once (<c>ProcessTrails</c> returns when <c>Sync.IsDedicated</c>): a read of the planet's storage under
    /// its lock for every step. Not made either.
    ///
    /// Here the closure is one per thread, reused, and the delegate and the queued call are not made. The damage calls
    /// queued on a hard hit do reach the closure (for the entity hit): they are given a copy of it instead.
    /// </summary>
    [PatchShim]
    public static class CharacterContactParticles
    {
        private const string InvokeName = "MyCharacter.RigidBody_ContactPointCallback.TrySpawnWalkingParticles";

        private static Type _closure;
        private static HashSet<FieldInfo> _links;
        private static MethodInfo _clone;
        [ThreadStatic] private static object _reused;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("CharacterContactParticles", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (!Sandbox.Engine.Platform.Game.IsDedicated) return;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var callback = typeof(MyCharacter).GetMethod("RigidBody_ContactPointCallback", any)
                           ?? throw new MissingMethodException("MyCharacter.RigidBody_ContactPointCallback");
            _closure = typeof(MyCharacter).GetNestedTypes(BindingFlags.NonPublic)
                           .SingleOrDefault(t => t.GetField("contactPointNormal", any) != null && t.GetField("otherPhysicsBody", any) != null)
                       ?? throw new MissingMemberException("MyCharacter: the closure of RigidBody_ContactPointCallback");
            // the other closures that reach it (the damage calls queued on a hard hit): they get a copy of their own
            _links = new HashSet<FieldInfo>(typeof(MyCharacter).GetNestedTypes(BindingFlags.NonPublic)
                .Where(t => t != _closure).SelectMany(t => t.GetFields(any)).Where(f => f.FieldType == _closure));
            _clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            ctx.GetPattern(callback).Transpilers.Add(typeof(CharacterContactParticles).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var original = instructions.ToList();
            var list = original.ToList();
            int closures = 0, delegates = 0, copies = 0, invokes = 0, links = 0, trails = 0;
            MsilInstruction Call(MsilInstruction old, string name)
            {
                var call = new MsilInstruction(OpCodes.Call).InlineValue(typeof(CharacterContactParticles).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var label in old.Labels) call.Labels.Add(label);
                return call;
            }
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].OpCode == OpCodes.Stfld && list[i].Operand is MsilOperandInline<FieldInfo> field && _links.Contains(field.Value))
                {
                    // a queued closure over this one: over a copy, which the next contact does not overwrite
                    var copy = new MsilInstruction(OpCodes.Call).InlineValue(typeof(CharacterContactParticles).GetMethod(nameof(Copy), BindingFlags.Static | BindingFlags.NonPublic));
                    list.Insert(i, copy);
                    list.Insert(i + 1, new MsilInstruction(OpCodes.Castclass).InlineValue(_closure));
                    i += 2;
                    links++;
                    continue;
                }
                if (!(list[i].Operand is MsilOperandInline<MethodBase> operand)) continue;
                var method = operand.Value;
                if (list[i].OpCode == OpCodes.Newobj && method.DeclaringType == _closure)
                {
                    // the closure: this thread's one, cast back to its type
                    list[i] = Call(list[i], nameof(Reused));
                    list.Insert(i + 1, new MsilInstruction(OpCodes.Castclass).InlineValue(_closure));
                    i++;
                    closures++;
                }
                else if (list[i].OpCode == OpCodes.Newobj && method.DeclaringType == typeof(Action) && i > 0 &&
                         list[i - 1].OpCode == OpCodes.Ldftn && list[i - 1].Operand is MsilOperandInline<MethodBase> target && target.Value.DeclaringType == _closure)
                {
                    if (list.Skip(i + 1).Take(2).Any(IsParticlesName))
                    {
                        // the walking particles: no delegate
                        list[i] = Call(list[i], nameof(NoDelegate));
                        delegates++;
                    }
                    else
                    {
                        // another queued call on the closure itself (the damage of a hard hit): on a copy of it
                        list.Insert(i - 1, new MsilInstruction(OpCodes.Call).InlineValue(typeof(CharacterContactParticles).GetMethod(nameof(Copy), BindingFlags.Static | BindingFlags.NonPublic)));
                        list.Insert(i, new MsilInstruction(OpCodes.Castclass).InlineValue(_closure));
                        i += 2;
                        copies++;
                    }
                }
                else if (method.Name == "CanProcessTrails" && method.DeclaringType == typeof(MyCharacter))
                {
                    // the footprints: ProcessTrails returns at once on a dedicated server, after the voxel material
                    // under the foot was read for it
                    list[i] = Call(list[i], nameof(NoTrails));
                    trails++;
                }
                else if (method.Name == "Invoke" && method.DeclaringType == typeof(MySandboxGame) && method.GetParameters().Length == 4 &&
                         method.GetParameters()[0].ParameterType == typeof(Action) &&
                         list.Skip(Math.Max(0, i - 4)).Take(Math.Min(i, 4)).Any(IsParticlesName))
                {
                    list[i] = Call(list[i], nameof(NoInvoke));
                    invokes++;
                }
            }
            // not what this was written for: the method as it is (a throw here would fail the patching of the game's
            // other methods too)
            if (closures != 1 || delegates != 1 || invokes != 1 || links != _links.Count || copies < 1 || trails != 1)
            {
                Log.Error($"CharacterContactParticles: {closures} closures, {delegates} delegates, {copies} copied delegates, {invokes} invokes, {trails} trails, " +
                          $"{links} of {_links.Count} links in RigidBody_ContactPointCallback; left as it is");
                return original;
            }
            return list;
        }

        private static bool IsParticlesName(MsilInstruction x) =>
            x.OpCode == OpCodes.Ldstr && x.Operand is MsilOperandInline<string> name && name.Value == InvokeName;

        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static object Reused() => _reused ?? (_reused = Activator.CreateInstance(_closure, true));

        private static object Copy(object closure) => _clone.Invoke(closure, null);

        private static bool NoTrails(MyCharacter character, Sandbox.Game.Entities.MyVoxelBase voxel) => false;

        private static Action NoDelegate(object target, IntPtr method) => null;

        private static void NoInvoke(MySandboxGame game, Action action, string name, int startAtFrame, int repeatTimes)
        {
        }
    }
}
