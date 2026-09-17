using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.ObjectBuilders.ComponentSystem;

namespace Optimizer.Optimizations
{
    /// <summary>
    /// Allocation-free equivalent of MyComponentContainer.Serialize for blocks without serialized
    /// components.
    ///
    /// Vanilla builds a temporary List for every component type of every block before it knows
    /// whether any component is serialized, then throws the lists away. It runs for every block of
    /// every grid on each world save (and on blueprint copies, grid replication...): measured 336
    /// bytes per conveyor block that ends up with no component container at all. The replacement
    /// first checks whether anything is serialized without allocating, then serializes the same
    /// components in the same order into the same container, returning null exactly when vanilla
    /// does. On any exception the vanilla method runs instead.
    /// </summary>
    [PatchShim]
    public static class ComponentContainerSerialize
    {
        private static readonly Func<MyComponentContainer, Dictionary<Type, List<MyComponentBase>>> Components =
            BuildComponentsGetter();

        public static void Patch(PatchContext ctx) =>
            global::SentisOptimisations.PatchGuard.Run("ComponentContainerSerialize", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            if (Components == null) throw new MissingFieldException("MyComponentContainer.m_components not found");
            var serialize = typeof(MyComponentContainer).GetMethod(nameof(MyComponentContainer.Serialize),
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            ctx.GetPattern(serialize).Prefixes.Add(typeof(ComponentContainerSerialize).GetMethod(nameof(SerializePrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Func<MyComponentContainer, Dictionary<Type, List<MyComponentBase>>> BuildComponentsGetter()
        {
            var field = typeof(MyComponentContainer).GetField("m_components", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return null;
            var container = Expression.Parameter(typeof(MyComponentContainer), "container");
            return Expression.Lambda<Func<MyComponentContainer, Dictionary<Type, List<MyComponentBase>>>>(
                Expression.Field(container, field), container).Compile();
        }

        private static bool SerializePrefix(MyComponentContainer __instance, bool copy,
            ref MyObjectBuilder_ComponentContainer __result)
        {
            try
            {
                __result = Serialize(Components(__instance), copy);
                return false;
            }
            catch (Exception e)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(e, "ComponentContainerSerialize failed, using vanilla");
                return true;
            }
        }

        public static MyObjectBuilder_ComponentContainer Serialize(Dictionary<Type, List<MyComponentBase>> components, bool copy)
        {
            var anySerialized = false;
            foreach (var entry in components)
            {
                foreach (var component in entry.Value)
                {
                    if (!component.IsSerialized()) continue;
                    anySerialized = true;
                    break;
                }
                if (anySerialized) break;
            }
            if (!anySerialized) return null;

            var result = new MyObjectBuilder_ComponentContainer();
            foreach (var entry in components)
            {
                foreach (var component in entry.Value)
                {
                    if (!component.IsSerialized()) continue;
                    var builder = component.Serialize(copy);
                    if (builder != null)
                        result.Components.Add(new MyObjectBuilder_ComponentContainer.ComponentData
                        {
                            TypeId = entry.Key.Name,
                            Component = builder,
                        });
                }
            }
            return result;
        }
    }
}
