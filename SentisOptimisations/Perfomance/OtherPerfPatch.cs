using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class OtherPerfPatch
    {
        public static void Patch(PatchContext ctx)
        {
            var MethodExecuteGasTransfer = typeof(MyTextPanelComponent).GetMethod
                ("SetDefaultTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodExecuteGasTransfer).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }
        
        private static bool Disabled()
        {
            return false;
        }
        
    }
}