using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.API.Plugins;
using Torch.Commands;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Definitions;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace NAPI
{
    public static class Ext2
    {
        public static MethodInfo _cachedDisableMethod = null;
        public static MethodInfo _cachedDisableMethodStatic = null;
        public const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static MyModStorageComponentBase GetStorage(this IMyEntity entity) { return entity.Storage = entity.Storage ?? new MyModStorageComponent(); }


        //MySession.

        public static bool IsRealPlayer(this MyIdentity myIdentity)
        {
            if (myIdentity == null)
            {
                return false;
            }

            if (!MySession.Static.Players.TryGetPlayerId(myIdentity.IdentityId, out MyPlayer.PlayerId playerid))
            {
                return false;
            }

            if ((MySession.Static.Players.IdentityIsNpc(myIdentity.IdentityId) || playerid.SteamId == 0))
            {
                return false;
            }
            return true;
        }

        public static bool IsAdmin(this MyIdentity myIdentity)
        {
            var id = MySession.Static.Players.TryGetSteamId(myIdentity.IdentityId);
            if (id == 0) return false;
            return MySession.Static.IsUserAdmin (id);
        }
        public static MethodInfo getMethod(this Type t, string name, BindingFlags flags = all, Type[] types = null)
        {
            if (types != null) { return t.GetMethod(name, types) ?? throw new Exception("Failed to find patch method:" + name); }

            return t.GetMethod(name, flags) ?? throw new Exception("Failed to find patch method" + name);
        }


        public static Type getTypeByName(string name)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(p => !p.IsDynamic);
			
			foreach (var assembly in assemblies) {
                try {
					var type = assembly.GetType(name);
                    if (type != null) { return type; }
                }
                catch (Exception e) { }
            }

            //Log.Error("Assemblies: " + assemblies.Count());
            //foreach (var assembly in assemblies)
            //{
            //    try { Log.Error(assembly.FullName); } catch (Exception e) { }
            //}

            throw new Exception("Type not found:" + name);
        }

        public static HashSet<Type> GetAllTypes ()
        {
            var allTypes = new HashSet <Type> ();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(p => !p.IsDynamic);
            foreach (var assembly in assemblies)
            {
                try
                {
                    var types = assembly.GetTypes();
                    foreach (var t in types)
                    {
                        allTypes.Add (t);
                    }
                }
                catch (Exception e) { }
            }


            return allTypes;
        }

        public static FieldInfo easyField(this Type type, String name)
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
            }

            easyField(instance.GetType(), name).SetValue(instance, value);
        }

        public static void PrintProperties(StringBuilder sb, Object cc)
        {
            var ms = cc.GetType().GetProperties(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                if (t.Name == "AuthKey" || t.Name == "RemoteDeletingText" || t.Name == "RemoteDeletingStrategyText") continue;
                var v = t.GetValue(cc);
                sb.Append("\n").Append(t.Name).Append("=").Append(v);
            }

            return;
        }

        public static MethodInfo easyMethod(this Type type, String name, bool needThrow = true)
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

        public static object easyCallMethod(this object instance, String name, object[] args, bool needThrow = true,
            Type type = null)
        {
            if (type != null)
            {
                return easyMethod(type, name, needThrow).Invoke(instance, args);
            }

            return easyMethod(instance.GetType(), name, needThrow).Invoke(instance, args);
        }

        public static MethodInfo easyMethod(this Type type, String name, int paramAmount, bool needThrow = true)
        {
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                if (t.Name == name && t.GetParameters().Length == paramAmount) { return t; }
            }

            if (needThrow) throw new Exception("Method " + name + " not found");
            return null;
        }


        public static MethodInfo singleMethod(this Type type)
        {
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                return t;
            }

            throw new Exception("Single method not found");
        }

        public static MethodInfo protectedMethod(this Type type, String[] names, Type[] types = null)
        {
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                var pars = t.GetParameters();
                if (pars.Length != names.Length) continue;

                bool ok = true;
                for (var x = 0; x < pars.Length; x++)
                {
                    if (types != null && pars[x].ParameterType != types[x])
                    {
                        ok = false;
                        break;
                    }

                    if (pars[x].Name != names[x])
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) { return t; }
            }

            String sb = "";
            foreach (var ss in names) { sb += ss + " "; }

            throw new Exception("Protected Method " + type.Name + " not found " + sb);
        }

        public static StringBuilder methodsPrint(this Type type)
        {
            var sb = new StringBuilder();
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                sb.Append($"{type.Name} -> {t.Name}");
            }
            return sb;
        }

        public static void RegisterCommands(this CommandManager manager, Type t, ITorchPlugin plugin, List<CommandAttribute> infos = null)
        {
            foreach (var method in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var commandAttrib = method.GetCustomAttribute<CommandAttribute>();
                if (commandAttrib != null)
                {
                    manager.Commands.AddCommand(new Command(plugin, method));
                    if (infos != null)
                    {
                        infos.Add (commandAttrib);
                    }
                }
            }
        }

        public static void GetCommandsInfo(Type t, List<CommandAttribute> infos = null)
        {
            foreach (var method in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var commandAttrib = method.GetCustomAttribute<CommandAttribute>();
                if (commandAttrib != null)
                {
                    if (infos != null)
                    {
                        infos.Add(commandAttrib);
                    }
                }
            }
        }


        public static MethodInfo easyPrivateMethod(this Type type, String name)
        {
            var ms = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var t in ms)
            {
                if (t.Name == name) { return t; }
            }
            throw new Exception("Method " + name + " not found");
        }


        public static bool TryGetEntityByNameOrId(string nameOrId, out IMyEntity entity)
        {
            if (long.TryParse(nameOrId, out var id)) return MyAPIGateway.Entities.TryGetEntityById(id, out entity);

            foreach (var ent in MyEntities.GetEntities())
            {
                if (ent.DisplayName != nameOrId) continue;
                entity = ent;
                return true;
            }

            entity = null;
            return false;
        }

        public static IMyPlayer GetPlayerByNameOrId(string nameOrPlayerId)
        {
            if (!long.TryParse(nameOrPlayerId, out long id))
            {
                foreach (var identity in MySession.Static.Players.GetAllIdentities())
                {
                    if (identity.DisplayName == nameOrPlayerId) { id = identity.IdentityId; }
                }
            }

            if (MySession.Static.Players.TryGetPlayerId(id, out MyPlayer.PlayerId playerId))
            {
                if (MySession.Static.Players.TryGetPlayerById(playerId, out MyPlayer player)) { return player; }
            }

            return null;
        }

        public static bool IsOnlyParts(this MyCubeGrid Grid)
        {
            var FatBlocks = Grid.CubeBlocks.Where
                    (x => (x.FatBlock != null));
            var blocks = FatBlocks.Where
                (x => x.FatBlock is IMyMotorAdvancedRotor || x.FatBlock is IMyMotorAdvancedStator || x.FatBlock is IMyMotorBase || x.FatBlock is IMyMotorRotor || x.FatBlock is IMyMotorStator || x.FatBlock is IMyMotorSuspension || x.FatBlock is IMyWheel || x.FatBlock is IMyPistonBase || x.FatBlock is IMyPistonTop || x.FatBlock is IMyExtendedPistonBase || x.FatBlock is MyWheel);
            return FatBlocks.Count() == blocks.Count();
        }
        public static bool HasBlockType(this IMyCubeGrid grid, string typeName)
        {
            foreach (var block in ((MyCubeGrid)grid).GetFatBlocks())
                if (string.Compare(block.BlockDefinition.Id.TypeId.ToString().Substring(16), typeName, StringComparison.InvariantCultureIgnoreCase) == 0)
                    return true;

            return false;
        }

        public static bool HasBlockSubtype(this IMyCubeGrid grid, string subtypeName)
        {
            foreach (var block in ((MyCubeGrid)grid).GetFatBlocks())
                if (string.Compare(block.BlockDefinition.Id.SubtypeName, subtypeName, StringComparison.InvariantCultureIgnoreCase) == 0)
                    return true;

            return false;
        }


        public static void Redirect(this PatchContext _ctx, Type t, Type t2, String name)
        {
            var meth1 = t.easyMethod(name);
            var meth2 = t2.easyMethod(name);
            _ctx.GetPattern(meth1).Prefixes.Add(meth2);
        }

        public static void Redirect(this PatchContext _ctx, Type t, String name, Type t2, String name2, BindingFlags flags1 = all, BindingFlags flags2 = all, Type[] types = null)
        {
            var meth1 = t.getMethod(name, flags1, types);
            var meth2 = t2.getMethod(name2, flags2, types);
            _ctx.GetPattern(meth1).Prefixes.Add(meth2);
        }

        public static void Redirect2(this PatchContext _ctx, Type t, String name, Type t2, String name2, BindingFlags flags1 = all, BindingFlags flags2 = all, Type[] types = null, Type[] types2 = null)
        {
            var meth1 = t.getMethod(name, flags1, types);
            var meth2 = t2.getMethod(name2, flags2, types2);
            _ctx.GetPattern(meth1).Prefixes.Add(meth2);
        }

        public static void Prefix2(this PatchContext _ctx, Type t, String name, Type t2, String[] names, Type[] types = null)
        {
            var meth1 = t.getMethod(name, all, types);
            var meth2 = t2.protectedMethod(names);
            _ctx.GetPattern(meth1).Prefixes.Add(meth2);
        }

        /// <summary>
        /// Methods run before the original method is run. If they return false the original method is skipped.
        /// </summary>
        /// <param name="_ctx"></param>
        /// <param name="t"></param>
        /// <param name="name"></param>
        /// <param name="t2"></param>
        /// <param name="names"></param>
        public static void Prefix(this PatchContext _ctx, Type t, String name, Type t2, String[] names)
        {
            try
            {
                _ctx.GetPattern(t.easyMethod(name)).Prefixes.Add(t2.protectedMethod(names));
            }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
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

        /// <summary>
        /// Methods run before the original method is run. If they return false the original method is skipped.
        /// </summary>
        /// <param name="_ctx"></param>
        /// <param name="t"></param>
        /// <param name="name"></param>
        /// <param name="t2"></param>
        /// <param name="name2"></param>
        public static void Prefix(this PatchContext _ctx, Type t, String name, Type t2, String name2)
        {
            try
            {
                _ctx.GetPattern(t.easyMethod(name)).Prefixes.Add(t2.easyMethod(name2));
            }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

        public static void Suffix(this PatchContext _ctx, Type t, Type t2, String name)
        {
            try { _ctx.GetPattern(t.easyMethod(name)).Suffixes.Add(t2.easyMethod(name)); }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

        public static void Suffix(this PatchContext _ctx, Type t, String name, Type t2, String[] names)
        {
            try { _ctx.GetPattern(t.easyMethod(name)).Suffixes.Add(t2.protectedMethod(names)); }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

        public static void Suffix(this PatchContext _ctx, Type t, String name, Type t2, String name2)
        {
            try { _ctx.GetPattern(t.easyMethod(name)).Suffixes.Add(t2.easyMethod(name2)); }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

        public static void Prefix(this PatchContext _ctx, Type t, String name, Type t2, String[] names, Type[] types)
        {
            try { _ctx.GetPattern(t.easyMethod(name)).Prefixes.Add(t2.protectedMethod(names)); }
            catch (Exception e)
            {
                throw new Exception("Failed patch :" + name + " " + t);
            }
        }

        public static void RegisterGuids(List<Guid> addGuids)
        {
            var def = MyDefinitionManager.Static.GetEntityComponentDefinitions<MyModStorageComponentDefinition>();
            var guids = def.FirstOrDefault().RegisteredStorageGuids.ToList();
            guids.AddList(addGuids);
            def.FirstOrDefault().RegisteredStorageGuids = guids.ToArray();
        }



        public static bool CanTransfer(IMyConveyorEndpointBlock start, IMyConveyorEndpointBlock endPoint, MyDefinitionId itemId, bool isPush)
        {
            return MyGridConveyorSystem.ComputeCanTransfer(start, endPoint, itemId); //MyGridConveyorSystem.CanTransfer()
        }
    }
}