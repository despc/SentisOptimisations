using System.Reflection;
using NLog;
using Sandbox.Engine.Voxels;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
     [PatchShim]
    public static class StreamingPatch
    {
    public static readonly Logger Log = LogManager.GetCurrentClassLogger();
    
    public static void Patch(PatchContext ctx)
    {
        var MyPhysicsLoadData = typeof(MyStorageBase).GetMethod
            ("GetData", BindingFlags.Instance | BindingFlags.NonPublic);
    
        ctx.GetPattern(MyPhysicsLoadData).Prefixes.Add(
            typeof(StreamingPatch).GetMethod(nameof(GetDataPatched),
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        
    }

    private static bool GetDataPatched(ref bool compressed)
    {
        if (SentisOptimisationsPlugin.Config.StreamingWithoutZip)
        {
            compressed = false;
        }
        return true;
    }
    }
}