using System;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Torch.Managers.PatchManager;
using VRage.Scripting.CompilerMethods;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class OtherPerfPatch
    {
        
        public static readonly Random r = new Random();
        public static void Patch(PatchContext ctx)
        {
            var MethodSetDefaultTexture = typeof(MyTextPanelComponent).GetMethod
                ("SetDefaultTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodSetDefaultTexture).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
                
            var MethodUpdateScreen = typeof(MyMultiTextPanelComponent).GetMethod
                (nameof(MyMultiTextPanelComponent.UpdateScreen), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodUpdateScreen).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Slowdown10),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
                    
            var MethodCockpitUpdateBeforeSimulation = typeof(MyCockpit).GetMethod
                (nameof(MyCockpit.UpdateBeforeSimulation), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            var MethodMyCryoChamberUpdateBeforeSimulation10 = typeof(MyCryoChamber).GetMethod
                (nameof(MyCryoChamber.UpdateBeforeSimulation10), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            var MethodMyCryoChamberUpdateAfterSimulation100 = typeof(MyCryoChamber).GetMethod
                (nameof(MyCryoChamber.UpdateAfterSimulation100), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodCockpitUpdateBeforeSimulation).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            ctx.GetPattern(MethodMyCryoChamberUpdateBeforeSimulation10).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));  
            
            ctx.GetPattern(MethodMyCryoChamberUpdateAfterSimulation100).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
            
            var MethodModPerfCounterEnterMethod = typeof(ModPerfCounter).GetMethod
                (nameof(ModPerfCounter.EnterMethod), BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            
            ctx.GetPattern(MethodModPerfCounterEnterMethod).Prefixes.Add(
                typeof(OtherPerfPatch).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool Disabled()
        {
            return false;
        }
        
        private static bool Slowdown10()
        {
            if (r.NextDouble() < 0.1)
            {
                return true;
            }

            return false;
        }
        
    }
}