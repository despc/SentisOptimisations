using System;
using System.Linq;
using System.Reflection;

namespace SentisOptimisations
{
    public static class ReflectionUtils
    {

    // (Type, name) caches: EasyField linearly scanned every field and InvokeInstanceMethod
            // redid GetMethod on every call; both run on per-tick paths (ship tools, turrets).
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,
                System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>> _easyFieldCache =
                new System.Collections.Concurrent.ConcurrentDictionary<Type,
                    System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>>();

            private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,
                System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>> _methodCache =
                new System.Collections.Concurrent.ConcurrentDictionary<Type,
                    System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>>();

            public static FieldInfo EasyField(this Type type, string name, bool needThrow = true)
            {
                var byName = _easyFieldCache.GetOrAdd(type, _ =>
                    new System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>());
                FieldInfo cached;
                if (byName.TryGetValue(name, out cached))
                {
                    return cached;
                }

                cached = type.EasyFieldUncached(name, false);
                if (cached != null)
                {
                    byName[name] = cached;
                    return cached;
                }

                if (needThrow)
                {
                    throw new Exception("Field " + name + " not found on " + type.Name);
                }

                return null;
            }

            internal static object InvokeInstanceMethod(Type type, object instance, string methodName, Object[] args)
            {
                var byName = _methodCache.GetOrAdd(type, _ =>
                    new System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>());
                MethodInfo cached;
                if (!byName.TryGetValue(methodName, out cached))
                {
                    BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Static;
                    cached = type.GetMethod(methodName, bindFlags);
                    if (cached != null)
                    {
                        byName[methodName] = cached;
                    }
                }

                return cached.Invoke(instance, args);
            }

        
        public const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        
        public const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static MethodInfo GetMethod(this Type type, string name, BindingFlags flags)
        {
            return type.GetMethod(name, flags) ?? throw new Exception($"Couldn't find method {name} on {type}");
        }

        public static MethodInfo[] GetMethods(this Type type, string name, BindingFlags flags)
        {
            return type.GetMethods(flags).Where(m => m.Name == name).ToArray();
        }

        public static MethodInfo GetInstanceMethod(this Type t, string name)
        {
            return GetMethod(t, name, InstanceFlags);
        }

        public static MethodInfo GetStaticMethod(this Type t, string name)
        {
            return GetMethod(t, name, StaticFlags);
        }
        
        
        public static void SetInstanceField(Type type, object instance, string fieldName, Object value)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            field.SetValue(instance, value);
        }
        
        public static object GetInstanceField(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
        
        public static object GetInstanceField(object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = instance.GetType().GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }
        private static FieldInfo EasyFieldUncached(this Type type, string name, bool needThrow = true)
        {
            var ms = type.GetFields(all);
            foreach (var t in ms)
            {
                if (t.Name == name) { return t; }
            }

            SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error("Field not found: " + name);
            foreach (var t in ms)
            {
                SentisOptimisationsPlugin.SentisOptimisationsPlugin.Log.Error(type.Name + " -> " + t.Name);
                if (t.Name == name) { return t; }
            }

            if (needThrow) throw new Exception("Field " + name + " not found");
            return null;
        }
        public static object GetPrivateStaticField(Type type, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(null);
        }
        
        public static void SetPrivateStaticField(Type type, string fieldName, Object value)
        {
            BindingFlags bindFlags = BindingFlags.Public | BindingFlags.NonPublic
                                                         | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            field.SetValue(null, value);
        }
        
        private static object InvokeInstanceMethodUncached(Type type, object instance, string methodName, Object[] args)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            var method = type.GetMethod(methodName, bindFlags);
            return method.Invoke(instance, args);
        }
        
        internal static object InvokeInstanceMethod(object instance, string methodName, Object[] args, Type genericType)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            var method = instance.GetType().GetMethod(methodName, bindFlags);
            method = method.MakeGenericMethod(genericType);
            return method.Invoke(instance, args);
        }
        
        internal static object InvokeStaticMethod(Type type, string methodName, Object[] args)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static;
            var method = type.GetMethod(methodName, bindFlags);
            return method.Invoke(null, args);
        }
    }
}