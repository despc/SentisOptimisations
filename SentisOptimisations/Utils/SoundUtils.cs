// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.SoundUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using Sandbox.Game;
using VRage.Audio;

namespace SentisOptimisations
{
    public static class SoundUtils
    {
        public static void SendTo(long playerId, MyGuiSounds sound = MyGuiSounds.HudGPSNotification3) =>
            MyVisualScriptLogicProvider.PlayHudSound(sound, playerId);

        public static void SendToAll(MyGuiSounds sound = MyGuiSounds.HudGPSNotification3) =>
            MyVisualScriptLogicProvider.PlayHudSound(sound);
    }
}