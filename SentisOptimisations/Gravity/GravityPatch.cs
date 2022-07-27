using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.GameSystems;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisOptimisationsPlugin
{
    [PatchShim]
    public static class GravityPatch
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static List<GravityPoint> gravityPoints = new List<GravityPoint>();
        
        public static void Patch(PatchContext ctx)
        {
            var Method = typeof(MyGravityProviderSystem).GetMethods(
                BindingFlags.Static | BindingFlags.Public).Where(info => info.Name.Contains("CalculateNaturalGravityInPoint")).ToList()[1];

            ctx.GetPattern(Method).Prefixes.Add(
                typeof(GravityPatch).GetMethod(nameof(CalculateNaturalGravityInPointPatched),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool CalculateNaturalGravityInPointPatched(Vector3D worldPoint,
            ref float naturalGravityMultiplier, ref Vector3 __result)
        {
            try
            {
                foreach (var gravityPoint in gravityPoints)
                {
                    if (Vector3D.Distance(worldPoint, gravityPoint.point) < gravityPoint.radius)
                    {
                        Vector3 gravityNormalized = (gravityPoint.point - worldPoint);
                        gravityNormalized.Normalize();
                        __result = gravityNormalized * 9.81f * gravityPoint.gravity;
                        naturalGravityMultiplier = gravityPoint.gravity;
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception in CalculateNaturalGravityInPointPatched", e);
            }

            return true;
        }
        public class GravityPoint
        {
            public GravityPoint(Vector3D point, float gravity, float radius)
            {
                this.point = point;
                this.gravity = gravity;
                this.radius = radius;
            }

            public Vector3D point;
            public float gravity;
            public float radius;

            public override string ToString()
            {
                return gravity + "G" + " Radius " + radius + " at " + point;
            }
        }
    }
}