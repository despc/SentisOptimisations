using System;
using System.Collections.Generic;
using NAPI;

namespace NAPI
{
    public static class FrameExecutor
    {
        private static int frame = 0;
        public static int currentFrame { get { return frame; } }

        private static readonly List<Action1<long>> onEachFrameLogic = new List<Action1<long>>();
        private static readonly List<Action1<long>> addOnEachFrameLogic = new List<Action1<long>>();
        private static readonly List<Action1<long>> removeOnEachFrameLogic = new List<Action1<long>>();
        private static bool needRemoveFrameLogic = false;

        public static void Update()
        {
            try
            {
                foreach (var x in onEachFrameLogic)
                {
                    x.run(frame);
                }

                onEachFrameLogic.AddList(addOnEachFrameLogic);
                foreach (var x in removeOnEachFrameLogic)
                {
                    onEachFrameLogic.Remove(x);
                }
                addOnEachFrameLogic.Clear();
                removeOnEachFrameLogic.Clear();
                frame++;
            }
            catch (Exception e)
            {
                //Log.ChatError("WTF", e);
            }
        }

        public static void addFrameLogic(Action1<long> action)
        {
            addOnEachFrameLogic.Add(action);
        }

        //But you cant remove it
        public static Action1<long> addFrameLogic(Action<long> action)
        {
            var wrapper = new ActionWrapper(action);
            addOnEachFrameLogic.Add(wrapper);
            return wrapper;
        }

        public static void removeFrameLogic(Action1<long> action)
        {
            removeOnEachFrameLogic.Add(action);
        }

        public static void addDelayedLogic(long frames, Action1<long> action)
        {
            addOnEachFrameLogic.Add(new DelayerAction(frames, action));
        }

        public static void addDelayedLogic(long frames, Action<long> action)
        {
            addOnEachFrameLogic.Add(new DelayerAction(frames, new ActionWrapper(action)));
        }

        private class ActionWrapper : Action1<long>
        {
            Action<long> action;
            public ActionWrapper(Action<long> action)
            {
                this.action = action;
            }

            public void run(long t)
            {
                action(t);
            }
        }

        private class DelayerAction : Action1<long>
        {
            private long timer;
            private Action1<long> action;
            public DelayerAction(long timer, Action1<long> action)
            {
                this.timer = timer;
                this.action = action;
            }


            public void run(long k)
            {
                if (timer > 0)
                {
                    timer--; return;
                }
                FrameExecutor.removeFrameLogic(this);
                action.run(k);
            }
        }
    }
}