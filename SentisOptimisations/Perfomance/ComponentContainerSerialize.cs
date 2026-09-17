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
    /// Leaner MyComponentContainer.Serialize.
    ///
    /// Vanilla builds a temporary List for every component type of every block before it knows
    /// whether any component is serialized, and creates a component container even when every
    /// serialized component returns no builder - e.g. conveyors, whose only "serialized" component is
    /// a hierarchy component without children. That empty container is dropped when the save is
    /// written. It runs for every block of every grid on each world save (and on blueprint copies,
    /// grid replication...): 336 bytes per conveyor block in vanilla, 0 here.
    ///
    /// The replacement serializes the same components in the same order and creates the container
    /// only for the first builder; with no builders it returns null, as vanilla does for blocks
    /// without serialized components (all consumers already handle a null container). On any
    /// exception the vanilla method runs instead.
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
            MyObjectBuilder_ComponentContainer result = null;
            foreach (var entry in components)
            {
                foreach (var component in entry.Value)
                {
                    if (!component.IsSerialized()) continue;
                    var builder = component.Serialize(copy);
                    if (builder == null) continue;
                    if (result == null) result = new MyObjectBuilder_ComponentContainer();
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
