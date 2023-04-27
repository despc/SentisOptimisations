using System;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Character;
using Torch.Managers.PatchManager;

namespace FixTurrets.Perfomance
{
    [PatchShim]
    public class ParallelUpdateTweaks
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static readonly Random r = new Random();

        public static void Patch(PatchContext ctx)
        {
            var MethodUpdateHeadAndWeapon = typeof(MyCharacter).GetMethod
                ("UpdateHeadAndWeapon", BindingFlags.Instance | BindingFlags.NonPublic);

            ctx.GetPattern(MethodUpdateHeadAndWeapon).Prefixes.Add(
                typeof(ParallelUpdateTweaks).GetMethod(nameof(Disabled),
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic));
        }

        private static bool Disabled()
        {
            return false;
        }
    }
}