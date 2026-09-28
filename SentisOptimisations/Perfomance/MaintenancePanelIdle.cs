using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Torch.Managers.PatchManager;

namespace SentisOptimisationsPlugin
{
    /// <summary>
    /// A maintenance panel's cover (access panels, server rack doors, crates, fridges, the first aid cabinet...) left alone
    /// on the dedicated server once it has nothing to do.
    ///
    /// <c>MyMaintenancePanelComponent</c> moves its cover through the render: while the cover's render actor is not there,
    /// its "once before the next frame" update asks for the next frame again, and <c>Open</c>/<c>Close</c> set "update the
    /// door instantly" and ask for it again too. A dedicated server renders nothing, the actor never comes, and every
    /// such cover on every grid ran that update (<c>UpdateDoorStatus</c>, <c>UpdateVisual</c>...) every frame for good:
    /// 1.2 ms of every frame on a world with many ships (<c>entities.once_before_frame</c> in the Watcher, 3.8 s of 150 s
    /// in dotTrace). All the loop did on the server, over and over, was its first pass: the status from "opening" to
    /// "open" (or "closing" to "closed").
    ///
    /// Here, on a dedicated server: the state from the save, a change of the status and the first look for the use
    /// objects run the game's own update as before; "update the door instantly" does that one step
    /// (<c>GoToNextPositionStage</c>) and is done; with nothing pending the call is left out.
    /// </summary>
    [PatchShim]
    public static class MaintenancePanelIdle
    {
        public enum Action
        {
            /// <summary>The game's own update runs.</summary>
            Game,
            /// <summary>The status goes to its final value and the instant update is done.</summary>
            FinishMove,
            /// <summary>Nothing to do: the call is left out.</summary>
            Skip,
        }

        private static Func<object, bool> _gameWork, _instant;
        private static Action<object> _finishMove;

        public static void Patch(PatchContext ctx) => global::SentisOptimisations.PatchGuard.Run("MaintenancePanelIdle", ctx, PatchImpl);

        internal static void PatchImpl(PatchContext ctx)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = AppDomain.CurrentDomain.GetAssemblies()
                           .Select(a => a.GetType("SpaceEngineers.Game.EntityComponents.Blocks.MyMaintenancePanelComponent", false))
                           .FirstOrDefault(t => t != null)
                       ?? throw new TypeLoadException("MyMaintenancePanelComponent");
            FieldInfo F(string name) => type.GetField(name, any) ?? throw new MissingFieldException(type.Name, name);
            var goToNext = type.GetMethod("GoToNextPositionStage", any) ?? throw new MissingMethodException(type.Name, "GoToNextPositionStage");

            var arg = Expression.Parameter(typeof(object), "component");
            var self = Expression.Convert(arg, type);
            // the game's own work: the state from the save, a status to apply, a position reached, the use objects not looked for yet
            var gameWork = Expression.OrElse(
                Expression.NotEqual(Expression.Field(self, F("m_builderToInitFrom")), Expression.Constant(null)),
                Expression.OrElse(Expression.Field(self, F("m_updateDoorStatus")),
                    Expression.OrElse(Expression.Field(self, F("m_positionReached")),
                        Expression.Equal(Expression.Field(self, F("m_useObjectsComponent")), Expression.Constant(null)))));
            _gameWork = Expression.Lambda<Func<object, bool>>(gameWork, arg).Compile();
            var instant = F("m_updateDoorStatusInstant");
            _instant = Expression.Lambda<Func<object, bool>>(Expression.Field(self, instant), arg).Compile();
            _finishMove = Expression.Lambda<Action<object>>(Expression.Block(
                Expression.Assign(Expression.Field(self, instant), Expression.Constant(false)),
                Expression.Call(self, goToNext)), arg).Compile();

            var method = type.GetMethod("OnUpdateOnceBeforeNextFrame", any) ?? throw new MissingMethodException(type.Name, "OnUpdateOnceBeforeNextFrame");
            ctx.GetPattern(method).Prefixes.Add(typeof(MaintenancePanelIdle).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>What a call does.</summary>
        public static Action Decide(bool dedicated, bool gameWork, bool instant)
        {
            if (!dedicated || gameWork) return Action.Game;
            return instant ? Action.FinishMove : Action.Skip;
        }

        private static bool Prefix(object __instance)
        {
            try
            {
                switch (Decide(Sandbox.Engine.Platform.Game.IsDedicated, _gameWork(__instance), _instant(__instance)))
                {
                    case Action.FinishMove:
                        _finishMove(__instance);
                        return false;
                    case Action.Skip:
                        return false;
                    default:
                        return true;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
