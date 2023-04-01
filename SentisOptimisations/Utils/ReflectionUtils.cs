using System;
using System.Linq;
using System.Reflection;

namespace SentisOptimisations
{
    public static class ReflectionUtils
    {
        
        public const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        
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
        
        internal static object InvokeInstanceMethod(Type type, object instance, string methodName, Object[] args)
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