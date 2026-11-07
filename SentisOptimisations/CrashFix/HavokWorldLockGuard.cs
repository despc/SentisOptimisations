using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A Havok world left locked for its critical operations does not take the server down.
    ///
    /// Havok puts off whatever changes a world while the world is locked (its step, a callback of its own); the lock is
    /// let go at the end and the put off changes are made. A world left locked for good - its step begun and not
    /// finished (see <c>PhysicsCost.Paired</c>), or a lock never let go - takes no body any more: each one put into it
    /// is left out, its add put off, and the game gives it gravity at once (<c>HkWorld.AddRigidBodyInternal</c> ->
    /// <c>HkRigidBody.AddGravity</c>): Havok's addAction reads the body's world, null, at +0xE8 - an access violation,
    /// and the game's handler of a crash kills the process without a word in Torch's log (the stand, 06-07.11.2026:
    /// eight times, a planet's new piece of voxel physics into the world of SentisClusters' cluster by the site, locked
    /// since some seconds or minutes before).
    ///
    /// Here, with the build of Havok.dll whose code it knows (the lock's count at +0xE8 of the world, read from its
    /// addAction):
    /// <list type="bullet">
    /// <item>after the frame's physics, on the game thread, a world locked more times than managed code holds it (its
    /// LockCriticalOperations without an UnlockCriticalOperations) is let go and its put off changes made - nothing of
    /// Havok runs then; a detached cluster's world (SentisClusters) is its tick's and is left alone;</item>
    /// <item>a body Havok did not take (no world of its own after its add): on the game thread outside the physics step,
    /// its world's stale lock let go and the body taken; otherwise it is not put into the world's lists nor given
    /// gravity - left out of the physics, where the game would have crashed.</item>
    /// </list>
    /// Each one written to the log (the first 20 of a kind, then each hundredth).
    /// </summary>
    [PatchShim]
    public static class HavokWorldLockGuard
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static readonly Harmony Harmony = new Harmony("SentisOptimisations.HavokWorldLockGuard");

        // hkpWorld::m_criticalOperationsLockCount; hkpWorldObject::m_world
        private const int LockOffset = 0xE8;
        private const int WorldOfBody = 0x10;

        private static readonly ConcurrentDictionary<IntPtr, int> ManagedLocks = new ConcurrentDictionary<IntPtr, int>();
        private static volatile bool _inStep;
        [ThreadStatic] private static int _addDepth;
        private static Func<HkWorld, bool> _isDetached;

        /// <summary>For the stand's tests: worlds let go after the step, bodies taken by letting go, bodies left out.</summary>
        internal static long Unlocked, Healed, LeftOut;

        /// <summary>For the stand's tests: the guard is on (Havok.dll's build known and the patches in).</summary>
        internal static bool On;

        private static long _unlockedTold, _healedTold, _leftOutTold;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("HavokWorldLockGuard", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            var known = KnownHavok(out var why);
            if (!known)
            {
                Log.Warn("HavokWorldLockGuard: off - " + why);
                return;
            }
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo Own(string name) => typeof(HavokWorldLockGuard).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            var world = typeof(HkWorld);
            Harmony.Patch(world.GetMethod("LockCriticalOperations", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("HkWorld.LockCriticalOperations"),
                prefix: new HarmonyMethod(Own(nameof(LockPrefix))));
            Harmony.Patch(world.GetMethod("UnlockCriticalOperations", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("HkWorld.UnlockCriticalOperations"),
                prefix: new HarmonyMethod(Own(nameof(UnlockPrefix))));
            Harmony.Patch(world.GetMethod("AddRigidBody", any, null, new[] { typeof(HkRigidBody) }, null) ?? throw new MissingMethodException("HkWorld.AddRigidBody"),
                prefix: new HarmonyMethod(Own(nameof(AddPrefix))), finalizer: new HarmonyMethod(Own(nameof(AddFinalizer))));
            Harmony.Patch(world.GetMethod("AddRigidBodyInternal", any, null, new[] { typeof(HkRigidBody) }, null) ?? throw new MissingMethodException("HkWorld.AddRigidBodyInternal"),
                prefix: new HarmonyMethod(Own(nameof(AddInternalPrefix))) { priority = Priority.First });
            var physics = typeof(MyPhysics);
            Harmony.Patch(physics.GetMethod("StepWorlds", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyPhysics.StepWorlds"),
                prefix: new HarmonyMethod(Own(nameof(StepPrefix))), finalizer: new HarmonyMethod(Own(nameof(StepFinalizer))));
            Harmony.Patch(physics.GetMethod("SimulateInternal", any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("MyPhysics.SimulateInternal"),
                postfix: new HarmonyMethod(Own(nameof(AfterPhysicsPostfix))));
            var detacher = AccessTools.TypeByName("SentisClusters.Run.Detacher")?.GetMethod("IsDetachedWorld", any, null, new[] { typeof(HkWorld) }, null);
            if (detacher != null) _isDetached = (Func<HkWorld, bool>)Delegate.CreateDelegate(typeof(Func<HkWorld, bool>), detacher);
            On = true;
            Log.Info("HavokWorldLockGuard: on (Havok.dll's lock at +0xE8 known" + (detacher != null ? "; SentisClusters' detached worlds left to their ticks)" : ")"));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string name);

        /// <summary>
        /// Havok.dll's HkRigidBody_AddGravityAction as the stand's build has it: mov rcx,[rcx+10h]; jmp hkpWorld::addAction,
        /// which starts push rbx; push rdi; sub rsp,78h; cmp dword ptr [rcx+0E8h],0 - the body's world at +0x10, the
        /// world's lock count at +0xE8.
        /// </summary>
        private static bool KnownHavok(out string why)
        {
            try
            {
                // loaded now if the game has not yet: its wrapper loads the same file on its first call
                var module = GetModuleHandle("Havok.dll");
                if (module == IntPtr.Zero)
                {
                    // by its full path: the process' own search path does not have the game's folder yet when the plugins patch
                    var folder = System.IO.Path.GetDirectoryName(typeof(HkWorld).Assembly.Location) ?? "";
                    module = LoadLibrary(System.IO.Path.Combine(folder, "Havok.dll"));
                }
                if (module == IntPtr.Zero) { why = "Havok.dll not found"; return false; }
                var add = GetProcAddress(module, "HkRigidBody_AddGravityAction");
                if (add == IntPtr.Zero) { why = "no HkRigidBody_AddGravityAction in Havok.dll"; return false; }
                var head = new byte[9];
                Marshal.Copy(add, head, 0, head.Length);
                if (head[0] != 0x48 || head[1] != 0x8B || head[2] != 0x49 || head[3] != 0x10 || head[4] != 0xE9)
                {
                    why = "Havok.dll's AddGravityAction is not the build this knows: " + BitConverter.ToString(head);
                    return false;
                }
                var target = add + 9 + BitConverter.ToInt32(head, 5);
                var body = new byte[14];
                Marshal.Copy(target, body, 0, body.Length);
                var expected = new byte[] { 0x40, 0x53, 0x57, 0x48, 0x83, 0xEC, 0x78, 0x83, 0xB9, 0xE8, 0x00, 0x00, 0x00, 0x00 };
                for (var i = 0; i < expected.Length; i++)
                    if (body[i] != expected[i])
                    {
                        why = "Havok.dll's addAction is not the build this knows: " + BitConverter.ToString(body);
                        return false;
                    }
                why = null;
                return true;
            }
            catch (Exception e)
            {
                why = "Havok.dll's code not read: " + e.Message;
                return false;
            }
        }

        private static int NativeLocks(HkWorld world) => world == null || world.IsDisposed ? 0 : Marshal.ReadInt32(world.NativeObject + LockOffset);

        private static int Held(HkWorld world) => ManagedLocks.TryGetValue(world.NativeObject, out var n) ? Math.Max(0, n) : 0;

        private static void LockPrefix(HkWorld __instance) => ManagedLocks.AddOrUpdate(__instance.NativeObject, 1, (_, n) => n + 1);

        private static void UnlockPrefix(HkWorld __instance) => ManagedLocks.AddOrUpdate(__instance.NativeObject, 0, (_, n) => n - 1);

        private static void StepPrefix() => _inStep = true;

        private static Exception StepFinalizer(Exception __exception)
        {
            _inStep = false;
            return __exception;
        }

        private static void AddPrefix() => _addDepth++;

        private static Exception AddFinalizer(Exception __exception)
        {
            _addDepth--;
            return __exception;
        }

        private static bool OnGameThread => Thread.CurrentThread == Sandbox.MySandboxGame.Static?.UpdateThread;

        private static bool Detached(HkWorld world)
        {
            try { return _isDetached != null && _isDetached(world); }
            catch { return true; }
        }

        /// <summary>The world's locks no managed code holds let go, and its put off changes made; how many were let go.</summary>
        private static int LetGo(HkWorld world)
        {
            var stale = NativeLocks(world) - Held(world);
            for (var i = 0; i < stale && i < 64; i++) world.UnlockCriticalOperations();
            ManagedLocks.AddOrUpdate(world.NativeObject, 0, (_, n) => n + stale);   // the unlocks above were not managed code's
            if (stale > 0) world.ExecutePendingCriticalOperations();
            return stale;
        }

        /// <summary>After the frame's physics: each world locked with no one holding it let go.</summary>
        private static void AfterPhysicsPostfix()
        {
            try
            {
                if (!OnGameThread) return;
                foreach (var cluster in MyPhysics.Clusters.GetClusters())
                {
                    if (!(cluster.UserData is HkWorld world) || world.IsDisposed) continue;
                    if (NativeLocks(world) <= Held(world) || Detached(world)) continue;
                    var locks = NativeLocks(world);
                    var stale = LetGo(world);
                    if (stale <= 0) continue;
                    var n = Interlocked.Increment(ref Unlocked);
                    if (Tell(ref _unlockedTold))
                        Log.Warn($"HavokWorldLockGuard: the world of cluster {cluster.ClusterId} ({world.RigidBodies.Count} bodies) was left locked after the physics " +
                                 $"({locks}, {locks - stale} held by the game): let go, its put off changes made - every body put into it would have crashed the server (#{n})");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "HavokWorldLockGuard: the check after the physics failed");
            }
        }

        /// <summary>A body after Havok's add: one Havok did not take is taken by letting a stale lock go, or left out.</summary>
        private static bool AddInternalPrefix(HkWorld __instance, HkRigidBody rigidBody)
        {
            try
            {
                if (rigidBody == null || rigidBody.IsDisposed || Marshal.ReadIntPtr(rigidBody.NativeObject + WorldOfBody) != IntPtr.Zero) return true;
                var locks = NativeLocks(__instance);
                var held = Held(__instance);
                var what = Describe(rigidBody);
                // a lock let go only where nothing of Havok's can hold it: the game thread, not in the step, not inside
                // another add's callback, a world no detached tick steps
                if (OnGameThread && !_inStep && _addDepth <= 1 && locks > held && !Detached(__instance))
                {
                    LetGo(__instance);
                    if (Marshal.ReadIntPtr(rigidBody.NativeObject + WorldOfBody) != IntPtr.Zero)
                    {
                        var n = Interlocked.Increment(ref Healed);
                        if (Tell(ref _healedTold))
                            Log.Warn($"HavokWorldLockGuard: {what} put into a world left locked ({locks}, {held} held by the game): the lock let go, the body taken - " +
                                     $"its gravity would have crashed the server (#{n})");
                        return true;
                    }
                }
                var left = Interlocked.Increment(ref LeftOut);
                if (Tell(ref _leftOutTold))
                    Log.Warn($"HavokWorldLockGuard: {what} not taken by Havok (its world locked {locks}, {held} held by the game" + (_inStep ? ", in the physics step" : "") +
                             (OnGameThread ? "" : ", thread " + Thread.CurrentThread.Name) + "): left out of the physics - its gravity would have crashed the server (#" + left + ")");
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "HavokWorldLockGuard: the check of a body's add failed");
                return true;
            }
        }

        private static string Describe(HkRigidBody body)
        {
            try
            {
                var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                return entity == null ? "a body" : entity.GetType().Name + " " + entity.EntityId + " '" + entity.DisplayName + "' at " + entity.PositionComp.GetPosition().ToString("F0");
            }
            catch
            {
                return "a body";
            }
        }

        private static bool Tell(ref long told)
        {
            var n = Interlocked.Increment(ref told);
            return n <= 20 || n % 100 == 0;
        }
    }
}
