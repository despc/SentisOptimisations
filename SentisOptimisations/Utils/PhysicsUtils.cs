using System.Collections.Generic;
using Havok;
using Sandbox.Engine.Physics;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisOptimisations.Utils
{
    public static class PhysicsUtils
    {
        public static IEnumerable<IMyEntity> GetEntities( HkWorld world)
        {
            List<IMyEntity> myEntityList = new List<IMyEntity>();
            foreach (HkEntity rigidBody in world.RigidBodies)
            {
                IMyEntity entity = rigidBody.GetBody().Entity;
                myEntityList.Add(entity);
            }
            return (IEnumerable<IMyEntity>) myEntityList;
        }
        
        public static bool IsTopMostParent<T>(MyEntity self)
        {
            return self.GetTopMostParent(typeof(T)) == self;
        }
    }
}