using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NAPI;
using NLog;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisOptimisations;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage;
using VRage.Game;
using VRage.Game.Definitions.SessionComponents;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [Category("so")]
    public class AdminCommands : CommandModule
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        
        [Command("gs", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void GenerateStations()
        {
            MySessionComponentEconomy mySessionComponentEconomy = MySession.Static.GetComponent<MySessionComponentEconomy>();

            Assembly ass = typeof(MySessionComponentEconomy).Assembly;
            Type MyStationGeneratorType = ass.GetType("Sandbox.Game.World.Generator.MyStationGenerator");
            // var CreateAsteroidShapeMethod = MyCompositeShapeProvider.GetMethod
            //     ("CreateAsteroidShape", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);

            PropertyInfo propertyEconomyDefinition = null;
            foreach (PropertyInfo property in typeof(MySessionComponentEconomy)
                         .GetProperties(BindingFlags.Instance | 
                                        BindingFlags.NonPublic |
                                        BindingFlags.Public))
            {
                if (property.Name.Contains("EconomyDefinition"))
                {
                    propertyEconomyDefinition = property;
                }
            }

            object EconomyDefinition = propertyEconomyDefinition.GetValue(mySessionComponentEconomy);
            object instance = Activator.CreateInstance(MyStationGeneratorType, new object[]{(MySessionComponentEconomyDefinition) EconomyDefinition});
            instance.easyCallMethod("GenerateStations", new object[] {MySession.Static.Factions});
            // new MyStationGenerator().GenerateStations(MySession.Static.Factions);
            // new MyFactionRelationGenerator((MySessionComponentEconomyDefinition) EconomyDefinition).GenerateFactionRelations(MySession.Static.Factions);
            
            mySessionComponentEconomy.easySetField("m_stationStoreItemsFirstGeneration", true);
            mySessionComponentEconomy.easyCallMethod("UpdateStations", new object[0]);
            mySessionComponentEconomy.easySetField("m_stationStoreItemsFirstGeneration", false);

        }
        
        [Command("spawnfield2", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void SpawnField2(int fieldSize, int count)
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            Log.Warn("Field Spawner: Start spawn field ");
            DoSpawnField2(player?.Character, count, fieldSize);
        }

        public void DoSpawnField2(IMyCharacter playerCharacter, int count, int fieldSize)
        {
            var myVoxelMapStorageDefinitions = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions()
                .Where(definition => definition.Id.SubtypeName.Contains("Field")).ToList();
            Type MyGuiScreenDebugSpawnMenuType =
                typeof(MyDefinitionManager).Assembly.GetType("Sandbox.Game.Gui.MyGuiScreenDebugSpawnMenu");

            for (int i = 0; i < count; i++)
            {
                Log.Warn("Field Spawner: Spawn  " + (i + 1) + "/" + count + " asteroid");
                var _random = new Random();

                var vxDef = myVoxelMapStorageDefinitions[_random.Next(0, myVoxelMapStorageDefinitions.Count)];

                var storageName = vxDef.Id.SubtypeName;
                MyStorageBase storage1 = (MyStorageBase)ReflectionUtils.InvokeStaticMethod(
                    MyGuiScreenDebugSpawnMenuType, "CreatePredefinedDataStorage",
                    new Object[] { storageName, null });

                var center = playerCharacter.PositionComp.GetPosition();
                BoundingSphereD boundingSphere = new BoundingSphereD(center, fieldSize);

                var randomToUniformPointInSphere = boundingSphere.RandomToUniformPointInSphere(_random.NextDouble(),
                    _random.NextDouble(), _random.NextDouble());
                Thread.Sleep(100);

                var storageRandomName = storageName + new Random().Next(0, 9999999);
                var asteroidEntityId = GetAsteroidEntityId(storageRandomName);
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        var storage = storage1;

                        var voxelMap = MyWorldGenerator.AddVoxelMap(storageRandomName, storage,
                            randomToUniformPointInSphere, asteroidEntityId);
                        if (voxelMap != null)
                        {
                            voxelMap.PositionComp.SetPosition(randomToUniformPointInSphere);
                            voxelMap.Name = storageRandomName;
                            voxelMap.AsteroidName = storageRandomName;
                            MyVoxelBase.StorageChanged OnStorageRangeChanged = (MyVoxelBase.StorageChanged)null;
                            OnStorageRangeChanged =
                                (MyVoxelBase.StorageChanged)((voxel, minVoxelChanged, maxVoxelChanged, changedData) =>
                                {
                                    voxelMap.Save = true;
                                    voxelMap.RangeChanged -= OnStorageRangeChanged;
                                });
                            voxelMap.RangeChanged += OnStorageRangeChanged;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception ", e);
                    }
                });
                Log.Warn("Field Spawner: Spawned  " + (i + 1) + "/" + count + " asteroid");
            }
        }
               
        [Command("spawnfield", ".", null)]
        [Permission(MyPromoteLevel.Moderator)]
        public void SpawnField(int fieldSize, int count, string materials, int radiusMin = 100, int radiusMax = 150)
        {
            var player = Context.Player;
            if (player?.Character == null)
                return;
            Log.Warn("Field Spawner: Start spawn field ");
            Task.Run(() => { DoSpawnField(player?.Character, count, fieldSize, materials, radiusMin, radiusMax); });
        }

        public void DoSpawnField(IMyCharacter playerCharacter, int count, int fieldSize, string materials,  int radiusMin, int radiusMax)
        {
            var materialsArray = materials.Split(',');
            Log.Warn("Field Spawner: Selected Ores " );
            foreach (var ore in materialsArray)
            {
                Log.Warn("Field Spawner: " + ore );
            }
            for (int i = 0; i < count; i++)
            {
                var i1 = i;
                Task.Run(() => GenerateAndSpawn(playerCharacter, count, fieldSize, radiusMin, radiusMax, i1, materialsArray));
                Thread.Sleep(1000);
            }
        }

        private void GenerateAndSpawn(IMyCharacter playerCharacter, int count, int fieldSize, int radiusMin, int radiusMax,
            int i, string[] materialsArray)
        {
            Log.Warn("Field Spawner: Spawn  " + (i + 1) + "/" + count + " asteroid");
            var _random = new Random();
            var seed = _random.Next(100, 10000000);
            var radius = _random.Next(radiusMin, radiusMax);
            string storageName = MakeStorageName("FieldAster-" + (object)seed + "r" + radius);
            var center = playerCharacter.PositionComp.GetPosition();
            BoundingSphereD boundingSphere = new BoundingSphereD(center, fieldSize);

            var randomToUniformPointInSphere = boundingSphere.RandomToUniformPointInSphere(_random.NextDouble(),
                _random.NextDouble(), _random.NextDouble());
            Thread.Sleep(100);

            
            var myCompositeShapeProvider = CreateAsteroidShape(seed, radius,
                new Random().Next(0, 9999999));

            MyStorageBase storage1 = (MyStorageBase)new MyOctreeStorage(
                (IMyStorageDataProvider)myCompositeShapeProvider,
                GetAsteroidVoxelSize((double)radius));


            Log.Warn("Field Spawner: Start replace material");
            ReplaceMaterial(materialsArray, (MyOctreeStorage)storage1);
            Log.Warn("Field Spawner: End replace material, start clone voxel data");
            var target = Clone((MyOctreeStorage)storage1);
            Log.Warn("Field Spawner: End clone voxel data");
            MyAPIGateway.Utilities.InvokeOnGameThread(() =>
            {
                try
                {
                    var storage = target;
                    var asteroidEntityId = GetAsteroidEntityId(storageName) + new Random().Next(10, 1000000);
                    var voxelMap =
                        MyWorldGenerator.AddVoxelMap(storageName, storage, randomToUniformPointInSphere, asteroidEntityId);
                    if (voxelMap != null)
                    {
                        voxelMap.PositionComp.SetPosition(randomToUniformPointInSphere);
                        voxelMap.Name = storageName;
                        voxelMap.AsteroidName = storageName;
                        MyVoxelBase.StorageChanged OnStorageRangeChanged = (MyVoxelBase.StorageChanged)null;
                        OnStorageRangeChanged =
                            (MyVoxelBase.StorageChanged)((voxel, minVoxelChanged, maxVoxelChanged, changedData) =>
                            {
                                voxelMap.Save = true;
                                voxelMap.RangeChanged -= OnStorageRangeChanged;
                            });
                        voxelMap.RangeChanged += OnStorageRangeChanged;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Exception ", e);
                }
            });
            Log.Warn("Field Spawner: Spawned  " + (i + 1) + "/" + count + " asteroid");
        }

        public MyOctreeStorage Clone(MyOctreeStorage source)
        {
            var target = new MyOctreeStorage(null, source.Size);
            target.Geometry.Init(target);
            return CloneCells(source, target);
             
        }

        private MyOctreeStorage CloneCells(MyOctreeStorage source, MyOctreeStorage target)
        {
            // For some reason the cacheSize will NOT work at the same size of the storage when less than 64.
            // This can be seen by trying to read the material, update and write back, then read again to verify.
            // Trying to adjust the size in BlockFillMaterial will only lead to memory corruption.
            // Normally I wouldn't recommend usig an oversized cache, but in this case it should not be an issue as we are changing the material for the entire voxel space.
            var cacheSize = Vector3I.Min(new Vector3I(64), source.Size * 2);

            Vector3I block;
            // read the asteroid in chunks to avoid the Arithmetic overflow issue.
            for (block.Z = 0; block.Z < source.Size.Z; block.Z += 64)
                for (block.Y = 0; block.Y < source.Size.Y; block.Y += 64)
                    for (block.X = 0; block.X < source.Size.X; block.X += 64)
                    {
                        var cache = new MyStorageData();
                        var cacheContent = new MyStorageData();
                        cache.Resize(cacheSize);
                        cacheContent.Resize(cacheSize);
                        // LOD1 is not detailed enough for content information on asteroids.
                        Vector3I maxRange = block + cacheSize - 1;
                        source.ReadRange(cache, MyStorageDataTypeFlags.Material, 0, block, maxRange);
                        source.ReadRange(cacheContent, MyStorageDataTypeFlags.Content, 0, block, maxRange);
                        target.WriteRange(cache, MyStorageDataTypeFlags.Material, block, maxRange);
                        target.WriteRange(cacheContent, MyStorageDataTypeFlags.Content, block, maxRange);
                    }

            return target;
        }
        private static Vector3I GetAsteroidVoxelSize(double asteroidRadius) => new Vector3I(Math.Max(64, (int) Math.Ceiling(asteroidRadius)));

        public static long GetAsteroidEntityId(string storageName) => storageName.GetHashCode64() & 72057594037927935L | 432345564227567616L;
        public static MyObjectBuilder_VoxelMap CreateAsteroidObjectBuilder(
            string storageName)
        {
            MyObjectBuilder_VoxelMap newObject = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_VoxelMap>();
            newObject.StorageName = storageName;
            newObject.PersistentFlags = MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
            newObject.PositionAndOrientation = new MyPositionAndOrientation?(MyPositionAndOrientation.Default);
            newObject.MutableStorage = false;
            return newObject;
        }

        public static IMyStorageDataProvider CreateAsteroidShape(int seed,
            float size,
            int generatorSeed = 0,
            int? generator = null)
        {
            Assembly ass = typeof(MyStorageBase).Assembly;
            Type MyCompositeShapeProvider = ass.GetType("Sandbox.Game.World.Generator.MyCompositeShapeProvider");
            var CreateAsteroidShapeMethod = MyCompositeShapeProvider.GetMethod
                ("CreateAsteroidShape", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var myCompositeShapeProvider = CreateAsteroidShapeMethod.Invoke(null,new object[]{seed, size, generatorSeed, generator});
            return (IMyStorageDataProvider) myCompositeShapeProvider;
        }
        
        
        public void ReplaceMaterial(string[] allowedMaterials, MyOctreeStorage m_storage)
        {
            var allowedMaterialsIndexes = allowedMaterials.ToList()
                .ConvertAll(input => MyDefinitionManager.Static.GetVoxelMaterialDefinition(input).Index);
            var stoneMaterialsIndexes = new string[] {"Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05"}
                .ToList().ConvertAll(input => MyDefinitionManager.Static.GetVoxelMaterialDefinition(input).Index);
            Vector3I block;
            var cacheSize = Vector3I.Min(new Vector3I(64), m_storage.Size);

            // read the asteroid in chunks of 64 to avoid the Arithmetic overflow issue.
            for (block.Z = 0; block.Z < m_storage.Size.Z; block.Z += 64)
            for (block.Y = 0; block.Y < m_storage.Size.Y; block.Y += 64)
            for (block.X = 0; block.X < m_storage.Size.X; block.X += 64)
            {
                var cache = new MyStorageData();
                cache.Resize(cacheSize);
                // LOD1 is not detailed enough for content information on asteroids.
                Vector3I maxRange = block + cacheSize - 1;
                m_storage.ReadRange(cache, MyStorageDataTypeFlags.Material, 0, block, maxRange);

                bool changed = false;
                Vector3I p;
                for (p.Z = 0; p.Z < cacheSize.Z; ++p.Z)
                for (p.Y = 0; p.Y < cacheSize.Y; ++p.Y)
                for (p.X = 0; p.X < cacheSize.X; ++p.X)
                {
                    var currentMaterial = cache.Material(ref p);
                    if (stoneMaterialsIndexes.Contains(currentMaterial)){
                       continue;
                    }
                    if (!allowedMaterialsIndexes.Contains(currentMaterial)){
                        cache.Material(ref p, allowedMaterialsIndexes[currentMaterial % allowedMaterialsIndexes.Count]);
                        changed = true;
                    }
                }

                if (changed)
                    m_storage.WriteRange(cache, MyStorageDataTypeFlags.Material, block, maxRange);
            }
        }
        
        public static MyStorageBase CreateProceduralAsteroidStorage(int seed, float radius)
        {
            Assembly ass = typeof(MyStorageBase).Assembly;
            Type MyCompositeShapeProvider = ass.GetType("Sandbox.Game.World.Generator.MyCompositeShapeProvider");
            var CreateAsteroidShapeMethod = MyCompositeShapeProvider.GetMethod
                ("CreateAsteroidShape", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var myCompositeShapeProvider = CreateAsteroidShapeMethod.Invoke(null,new object[]{seed, radius, 0, null});
            return (MyStorageBase)
                new MyOctreeStorage((IMyStorageDataProvider) myCompositeShapeProvider,
                    MyVoxelCoordSystems.FindBestOctreeSize(radius));
        }

        public static string MakeStorageName(string storageNameBase)
        {
            string str = storageNameBase;
            int num = 0;
            bool flag;
            do
            {
                flag = false;
                foreach (MyVoxelBase instance in MySession.Static.VoxelMaps.Instances)
                {
                    if (instance.StorageName == str)
                    {
                        flag = true;
                        break;
                    }
                }
                if (flag)
                    str = storageNameBase + "-" + (object) num++;
            }
            while (flag);
            return str;
        }
        
    }
}