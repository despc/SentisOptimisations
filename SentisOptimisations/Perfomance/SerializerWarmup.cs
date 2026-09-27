using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NLog;
using VRage.Game;
using VRage.Library.Collections;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Serialization;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// The serializers the first joining player would otherwise build are made while the world loads.
    ///
    /// Handing a replicable to a client writes the entity into the creation packet with
    /// <c>MySerializer.Write(..., MyObjectBuilderSerializerKeen.Dynamic)</c>, and the very first write
    /// of a type builds its serializer. Measured on the stand: the first character sent to the first
    /// client cost <b>66 ms</b> inside the frame, while every later one cost 0.03 ms - the same
    /// warm-up is paid for grids and floating objects. It is a one-off, but it lands squarely on the
    /// frame in which somebody joins.
    ///
    /// So the same write is done here once at world load, into a stream that is thrown away, where
    /// nobody is watching the frame rate.
    ///
    /// The same goes for the arguments of the game's network events (the methods marked <c>[Event]</c>): the first
    /// placement of a block after the start sent its <c>BuildBlocksClient</c> with serializers built right then - the
    /// first placement cost 60 ms on the stand, 10 ms with them built at the start. Each argument type of every event
    /// is written once here as well; a type that cannot be made up is left to its first use, as before.
    /// </summary>
    public static class SerializerWarmup
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static bool _done;

        /// <summary>Milliseconds of serializer building kept out of the first join.</summary>
        public static double WarmedMs { get; private set; }

        public static void Run()
        {
            if (_done) return;
            _done = true;

            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                using (var stream = new BitStream())
                {
                    stream.ResetWrite();
                    Warm<MyObjectBuilder_Character>(stream);
                    Warm<MyObjectBuilder_CubeGrid>(stream);
                    Warm<MyObjectBuilder_FloatingObject>(stream, floating =>
                        floating.Item = new MyObjectBuilder_InventoryItem
                        {
                            Amount = 1,
                            PhysicalContent = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Stone"),
                        });
                }

                var (warmed, failed) = WarmEventArguments();
                WarmedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
                if (global::SentisOptimisations.DiagLog.On) Log.Info($"Serializers warmed up in {WarmedMs:F0} ms: object builders, and {warmed} argument types of the network events ({failed} left to their first use)");
            }
            catch (Exception e)
            {
                // Nothing is lost: the first join builds the serializer as it always did.
                Log.Warn(e, "Could not warm up the object builder serializers");
            }
        }

        /// <summary>
        /// Writes one builder of the type. It has to look like one the game really sends: a member
        /// the serializer does not allow to be null - a character's battery, and inside it the
        /// regeneration effects - stops the write right there, and every member after it goes
        /// unwarmed. So every empty member is given an empty value first (<see cref="FillNulls"/>);
        /// what cannot be made up that way, like the abstract content of an item, the caller fills.
        /// </summary>
        private static void Warm<T>(BitStream stream, Action<T> fill = null) where T : MyObjectBuilder_Base, new()
        {
            try
            {
                var builder = MyObjectBuilderSerializerKeen.CreateNewObject<T>();
                if (builder == null) return;
                fill?.Invoke(builder);
                FillNulls(builder, 0, new HashSet<object>());
                var asBase = (MyObjectBuilder_Base)builder;
                MySerializer.Write(stream, ref asBase, MyObjectBuilderSerializerKeen.Dynamic);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not warm up the serializer of " + typeof(T).Name);
            }
        }

        /// <summary>Every argument type of the game's network events written once; how many were, and how many could not be.</summary>
        private static (int Warmed, int Failed) WarmEventArguments()
        {
            var eventAttribute = typeof(VRage.Network.EventAttribute);
            var write = typeof(MySerializer).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .First(m => m.Name == nameof(MySerializer.Write) && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
            var types = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (!name.StartsWith("Sandbox") && !name.StartsWith("SpaceEngineers") && !name.StartsWith("VRage")) continue;
                Type[] all;
                try { all = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
                foreach (var type in all)
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsDefined(eventAttribute, false)) continue;
                    foreach (var parameter in method.GetParameters())
                    {
                        var t = parameter.ParameterType;
                        if (t.IsByRef || t.ContainsGenericParameters || t.IsPointer || t == typeof(object)) continue;
                        types.Add(t);
                    }
                }
            }
            int warmed = 0, failed = 0;
            using (var stream = new BitStream())
            {
                foreach (var type in types)
                {
                    stream.ResetWrite();
                    try
                    {
                        var value = type.IsValueType ? Activator.CreateInstance(type) : Empty(type);
                        if (value != null && !type.IsValueType) FillNulls(value, 0, new HashSet<object>());
                        write.MakeGenericMethod(type).Invoke(null, new[] { stream, value, null });
                        warmed++;
                    }
                    catch
                    {
                        // the serializer is built before the write; a value it will not take still leaves it built
                        failed++;
                    }
                }
            }
            return (warmed, failed);
        }

        private const int FillDepth = 4;

        /// <summary>
        /// Empty strings, arrays and collections, and default-constructed objects, for every member
        /// left null - on the builder and on what it contains, a few levels down.
        /// </summary>
        private static void FillNulls(object target, int depth, HashSet<object> seen)
        {
            if (target == null || depth > FillDepth || !seen.Add(target)) return;
            var type = target.GetType();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsInitOnly || field.FieldType.IsValueType) continue;
                var value = field.GetValue(target) ?? Empty(field.FieldType);
                if (value == null) continue;
                if (field.GetValue(target) == null) field.SetValue(target, value);
                FillNulls(value, depth + 1, seen);
            }
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType.IsValueType || property.GetIndexParameters().Length > 0 ||
                    property.GetGetMethod() == null || property.GetSetMethod() == null)
                    continue;
                object current;
                try { current = property.GetValue(target); }
                catch { continue; }
                var value = current ?? Empty(property.PropertyType);
                if (value == null) continue;
                if (current == null)
                {
                    try { property.SetValue(target, value); }
                    catch { continue; }
                }
                FillNulls(value, depth + 1, seen);
            }
        }

        private static object Empty(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            if (type.IsArray) return Array.CreateInstance(type.GetElementType(), 0);
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) return null;
            if (type.GetConstructor(Type.EmptyTypes) == null) return null;
            try
            {
                return typeof(MyObjectBuilder_Base).IsAssignableFrom(type)
                    ? MyObjectBuilderSerializerKeen.CreateNewObject(type)
                    : Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Starts over for a new world.</summary>
        public static void Reset() => _done = false;
    }
}
