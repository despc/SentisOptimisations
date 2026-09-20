using System;
using System.Reflection;
using System.Reflection.Emit;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// Delegates for the private parts of the game, built once.
    ///
    /// A patch that runs on every frame, every block or every network packet cannot afford
    /// <c>FieldInfo.GetValue</c> - it boxes, it walks reflection metadata and it is an order of
    /// magnitude slower than the field access it stands for. These build that access as IL once and
    /// hand back a delegate.
    /// </summary>
    public static class Accessors
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Reads an instance field, by name, as <typeparamref name="TField"/>.</summary>
        public static Func<TOwner, TField> Field<TOwner, TField>(string name)
        {
            var field = typeof(TOwner).GetField(name, Any);
            if (field == null) throw new MissingFieldException(typeof(TOwner).FullName, name);
            var method = new DynamicMethod("Get_" + typeof(TOwner).Name + "_" + name, typeof(TField),
                new[] { typeof(TOwner) }, typeof(TOwner), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            if (typeof(TField) == typeof(object) && field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
            il.Emit(OpCodes.Ret);
            return (Func<TOwner, TField>)method.CreateDelegate(typeof(Func<TOwner, TField>));
        }

        /// <summary>
        /// Reads an instance field of a type that cannot be named at compile time - an internal
        /// class of the game, say. The instance is passed as an object and cast inside.
        /// </summary>
        public static Func<object, TField> FieldOn<TField>(Type owner, string name)
        {
            var field = owner.GetField(name, Any);
            if (field == null) throw new MissingFieldException(owner.FullName, name);
            var method = new DynamicMethod("Get_" + owner.Name + "_" + name, typeof(TField),
                new[] { typeof(object) }, owner, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, field);
            if (typeof(TField) == typeof(object) && field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
            il.Emit(OpCodes.Ret);
            return (Func<object, TField>)method.CreateDelegate(typeof(Func<object, TField>));
        }

        /// <summary>Writes an instance field, by name.</summary>
        public static Action<TOwner, TField> SetField<TOwner, TField>(string name)
        {
            var field = typeof(TOwner).GetField(name, Any);
            if (field == null) throw new MissingFieldException(typeof(TOwner).FullName, name);
            var method = new DynamicMethod("Set_" + typeof(TOwner).Name + "_" + name, null,
                new[] { typeof(TOwner), typeof(TField) }, typeof(TOwner), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<TOwner, TField>)method.CreateDelegate(typeof(Action<TOwner, TField>));
        }

        /// <summary>
        /// Binds an instance method to <typeparamref name="TDelegate"/>, whose first parameter is the
        /// instance. The method may be private and may be declared by a base type.
        /// </summary>
        public static TDelegate Method<TOwner, TDelegate>(string name) where TDelegate : class
        {
            var method = typeof(TOwner).GetMethod(name, Any);
            if (method == null) throw new MissingMethodException(typeof(TOwner).FullName, name);
            return Delegate.CreateDelegate(typeof(TDelegate), method) as TDelegate;
        }

        /// <summary>
        /// The same for a type that cannot be named at compile time: the instance is passed as an
        /// object and cast inside.
        /// </summary>
        public static TDelegate MethodOn<TDelegate>(Type owner, string name) where TDelegate : class
        {
            var method = owner.GetMethod(name, Any);
            if (method == null) throw new MissingMethodException(owner.FullName, name);
            return Delegate.CreateDelegate(typeof(TDelegate), method) as TDelegate;
        }
    }
}
