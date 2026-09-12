using System;
using System.Reflection;
using Torch.Managers.PatchManager;

namespace NAPI
{
    public static class Ext2
    {

    // Members of a loaded Type never change: cache the (Type, name) resolutions because the
    // ship-tool/replication hot paths resolve the same members every tick.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>> FieldCache =
    new System.Collections.Concurrent.ConcurrentDictionary<Type, System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>>();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>> MethodCache =
    new System.Collections.Concurrent.ConcurrentDictionary<Type, System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>>();

    public static FieldInfo easyField(this Type type, String name)
    {
        var byName = FieldCache.GetOrAdd(type, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, FieldInfo>());
        FieldInfo cached;
        if (byName.TryGetValue(name, out cached))
        {
            return cached;
        }

        cached = type.ResolveFieldUncached(name); // throws when not found
        byName[name] = cached;
        return cached;
    }

    public static MethodInfo easyMethod(this Type type, String name, bool needThrow = true)
    {
        var byName = MethodCache.GetOrAdd(type, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>());
        MethodInfo cached;
        if (byName.TryGetValue(name, out cached))
        {
            return cached;
        }

        cached = type.ResolveMethodUncached(name, needThrow);
        if (cached != null)
        {
            byName[name] = cached;
        }

        return cached;
    }

        private static FieldInfo ResolveFieldUncached(this Type type, String name)
        {
            var fieldInfo = type.GetField(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fieldInfo != null)
            {
                return fieldInfo;
            }
            var ms = type.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

            throw new Exception("Field " + name + " not found");
        }
        public static object easyGetField(this object instance, String name, Type type = null)
        {
            if (type != null)
            {
                return easyField(type, name).GetValue(instance);
            }
            return easyField(instance.GetType(), name).GetValue(instance);
        }
        
        public static void easySetField(this object instance, String name, object value, Type type = null)
        {
            if (type != null)
            {
                easyField(type, name).SetValue(instance, value);
         return; // was missing: value applied twice (base type, then derived type)
            }

            easyField(instance.GetType(), name).SetValue(instance, value);
        }


        private static MethodInfo ResolveMethodUncached(this Type type, String name, bool needThrow = true)
        {
            var methodInfo = type.GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (methodInfo != null)
            {
                return methodInfo;
            }
            
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            { //FUCK THIS SHIT
                if (t.Name == name) { return t; }
            }

            if (needThrow) throw new Exception("Method " + name + " not found");
            return null;
        }

        public static object easyCallMethod(this object instance, String name, object[] args = null, bool needThrow = true,
            Type type = null)
        {
            if (args == null)
            {
                args = new object[] { };
            }
            if (type != null)
            {
                return easyMethod(type, name, needThrow).Invoke(instance, args);
            }

            return easyMethod(instance.GetType(), name, needThrow).Invoke(instance, args);
        }

        /// <summary>
        /// Methods run before the original method is run. If they return false the original method is skipped.
        /// </summary>
        /// <param name="_ctx"></param>
        /// <param name="t"></param>
        /// <param name="t2"></param>
        /// <param name="name"></param>
        public static void Prefix(this PatchContext _ctx, Type t, Type t2, String name)
        {
            try
            {
                _ctx.GetPattern(t.easyMethod(name)).Prefixes.Add(t2.easyMethod(name));
            }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

    }
}