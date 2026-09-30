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
            CheckRead(field, typeof(TField));
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
            CheckRead(field, typeof(TField));
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

        /// <summary>
        /// The IL below does not convert: a field read or written as another type is its memory taken for that type -
        /// a number or a structure taken for an object is a reference the collector follows (see SentisTests
        /// struct_this_gc). So the types are checked once, here.
        /// </summary>
        public static void CheckRead(FieldInfo field, Type taken)
        {
            if (taken == typeof(object) || Compatible(field.FieldType, taken)) return;
            throw new InvalidCastException(field.DeclaringType?.FullName + "." + field.Name + " is " + field.FieldType.FullName + ", not read as " + taken.FullName);
        }

        /// <summary>As <see cref="CheckRead"/>, for a value written into the field.</summary>
        public static void CheckWrite(FieldInfo field, Type written)
        {
            if (Compatible(written, field.FieldType)) return;
            throw new InvalidCastException(field.DeclaringType?.FullName + "." + field.Name + " is " + field.FieldType.FullName + ", not written as " + written.FullName);
        }

        /// <summary>A value of <paramref name="from"/> stored as <paramref name="to"/> without conversion.</summary>
        public static bool Compatible(Type from, Type to)
        {
            if (from == to) return true;
            if (from.IsValueType || to.IsValueType)
                return from.IsEnum && Enum.GetUnderlyingType(from) == to || to.IsEnum && Enum.GetUnderlyingType(to) == from;
            return to.IsAssignableFrom(from);
        }

        /// <summary>Writes an instance field, by name.</summary>
        public static Action<TOwner, TField> SetField<TOwner, TField>(string name)
        {
            var field = typeof(TOwner).GetField(name, Any);
            if (field == null) throw new MissingFieldException(typeof(TOwner).FullName, name);
            CheckWrite(field, typeof(TField));
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
