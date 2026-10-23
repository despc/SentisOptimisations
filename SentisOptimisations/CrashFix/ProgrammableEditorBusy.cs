using System;
using System.Reflection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRage.Network;

namespace SentisOptimisationsPlugin.CrashFix
{
    /// <summary>
    /// A player who opens the editor of a programmable block someone already has open is told it is busy, not dropped.
    ///
    /// <c>MyProgrammableBlock.OpenEditorRequest</c> answers "busy" with <c>RaiseEvent(this, x => OpenEditorFailure)</c>,
    /// but <c>OpenEditorFailure</c> is a static event: the replication layer looks for it among the block's events, does
    /// not find it and throws ("Event 'OpenEditorFailure' not found"); the receive queue drops the client whose packet
    /// threw. So everyone who opened a block's editor while another player had it open was disconnected - and so was the
    /// holder who asked twice (a second click before the answer came). Seen on prod.
    ///
    /// Here: the holder asking again, or the holder gone from the server, takes the editor as the game would give a
    /// closed one; anyone else gets the "busy" event sent the way a static event goes, to them (the game would address
    /// it to the holder). Check: regression <c>reg_programmable_editor</c>.
    /// </summary>
    [PatchShim]
    public static class ProgrammableEditorBusy
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo _open;
        private static PropertyInfo _holder;
        private static Func<IMyEventOwner, Action> _busy;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("ProgrammableEditorBusy", ctx, c =>
        {
            var type = typeof(MyProgrammableBlock);
            _open = type.GetField("m_consoleOpen", Any) ?? throw new MissingFieldException("MyProgrammableBlock.m_consoleOpen");
            _holder = type.GetProperty("UserId", Any) ?? throw new MissingMemberException("MyProgrammableBlock.UserId");
            var failure = type.GetMethod("OpenEditorFailure", Any) ?? throw new MissingMethodException("MyProgrammableBlock.OpenEditorFailure");
            var busy = (Action)Delegate.CreateDelegate(typeof(Action), failure);
            _busy = owner => busy;
            var request = type.GetMethod("OpenEditorRequest", Any) ?? throw new MissingMethodException("MyProgrammableBlock.OpenEditorRequest");
            c.GetPattern(request).Prefixes.Add(typeof(ProgrammableEditorBusy).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        });

        private static bool Prefix(MyProgrammableBlock __instance)
        {
            if (!(bool)_open.GetValue(__instance)) return true;
            var sender = MyEventContext.Current.Sender.Value;
            var holder = (ulong)_holder.GetValue(__instance);
            if (holder == sender || Sync.Clients == null || !Sync.Clients.HasClient(holder))
            {
                // the game's own branch for a closed editor gives it to the sender
                _open.SetValue(__instance, false);
                return true;
            }
            MyMultiplayer.RaiseStaticEvent(_busy, new EndpointId(sender));
            return false;
        }
    }
}
