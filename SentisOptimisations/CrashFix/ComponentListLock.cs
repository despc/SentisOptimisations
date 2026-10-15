using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A projection's component count is no longer written from two threads at once.
    ///
    /// From the server (14.10.2026, players welding large ships): a worker thread spent the whole 13 s of a profile in
    /// one <c>Dictionary.TryGetValue</c> under <c>MyComponentList.AddMaterial</c>, from
    /// <c>MyGridClipboard.ChangeClipboardPreview</c>. A projector's clipboard builds its preview on a worker
    /// (<c>Activate</c>, <c>ActivateNoAlign</c>: <c>Parallel.Start</c>), and there clears and fills its
    /// <c>m_buildComponents</c>; the game thread removing the projection meanwhile (<c>RemoveProjection</c> -
    /// <c>Deactivate</c>, <c>Hide</c>) clears the same list. A <c>Dictionary</c> cleared while an entry is added can be
    /// left with its chain of entries in a loop, and every lookup in it then runs for ever: the worker is lost until
    /// the server restarts, and the projector's activation never completes. <c>AddMaterial</c> and <c>Clear</c> are done
    /// here under a lock of the list: the counts may come out of an interrupted fill (the dedicated server does not
    /// show them), the dictionaries stay whole.
    /// </summary>
    [PatchShim]
    public static class ComponentListLock
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Func<MyComponentList, List<MyTuple<MyDefinitionId, int>>> DisplayList =
            Getter<List<MyTuple<MyDefinitionId, int>>>("m_displayList");
        private static readonly Func<MyComponentList, Dictionary<MyDefinitionId, int>> TotalMaterials =
            Getter<Dictionary<MyDefinitionId, int>>("m_totalMaterials");
        private static readonly Func<MyComponentList, Dictionary<MyDefinitionId, int>> RequiredMaterials =
            Getter<Dictionary<MyDefinitionId, int>>("m_requiredMaterials");

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ComponentListLock", ctx, c =>
        {
            if (DisplayList == null || TotalMaterials == null || RequiredMaterials == null)
                throw new MissingFieldException("MyComponentList: m_displayList, m_totalMaterials or m_requiredMaterials");
            var self = typeof(ComponentListLock);
            c.GetPattern(typeof(MyComponentList).GetMethod(nameof(MyComponentList.AddMaterial), Any)
                         ?? throw new MissingMethodException("MyComponentList.AddMaterial"))
                .Prefixes.Add(self.GetMethod(nameof(AddMaterialPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            c.GetPattern(typeof(MyComponentList).GetMethod(nameof(MyComponentList.Clear), Any)
                         ?? throw new MissingMethodException("MyComponentList.Clear"))
                .Prefixes.Add(self.GetMethod(nameof(ClearPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static Func<MyComponentList, T> Getter<T>(string name)
        {
            var field = typeof(MyComponentList).GetField(name, Any);
            if (field == null || field.FieldType != typeof(T)) return null;
            var method = new DynamicMethod("Get_" + name, typeof(T), new[] { typeof(MyComponentList) }, typeof(MyComponentList), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<MyComponentList, T>)method.CreateDelegate(typeof(Func<MyComponentList, T>));
        }

        // (as the game's AddMaterial and AddToDictionary, under the list's lock)
        private static bool AddMaterialPrefix(MyComponentList __instance, MyDefinitionId myDefinitionId, int amount, int requiredAmount, bool addToDisplayList)
        {
            lock (__instance)
            {
                if (requiredAmount > amount) requiredAmount = amount;
                if (addToDisplayList) DisplayList(__instance).Add(new MyTuple<MyDefinitionId, int>(myDefinitionId, amount));
                Add(TotalMaterials(__instance), myDefinitionId, amount);
                if (requiredAmount > 0) Add(RequiredMaterials(__instance), myDefinitionId, requiredAmount);
            }
            return false;
        }

        private static bool ClearPrefix(MyComponentList __instance)
        {
            lock (__instance)
            {
                DisplayList(__instance).Clear();
                TotalMaterials(__instance).Clear();
                RequiredMaterials(__instance).Clear();
            }
            return false;
        }

        private static void Add(Dictionary<MyDefinitionId, int> materials, MyDefinitionId id, int amount)
        {
            materials.TryGetValue(id, out var value);
            materials[id] = value + amount;
        }
    }
}
