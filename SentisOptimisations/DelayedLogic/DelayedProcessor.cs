using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace SentisOptimisations.DelayedLogic
{
    public class DelayedProcessor
    {
        public static DelayedProcessor Instance;
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private SortedList<DateTime, Action> _actions = new SortedList<DateTime, Action>();
        private Object _lock = new object();

        public CancellationTokenSource CancellationTokenSource { get; set; }


        public void AddDelayedAction(DateTime time, Action action)
        {
            lock (_lock)
            {
                while (_actions.ContainsKey(time))
                {
                    time = time.AddMilliseconds(1);
                }
                _actions.Add(time, action);
            }
        }

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            Task.Run(DelayedLogicLoop);
        }

        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }

        public void DelayedLogicLoop()
        {
            try
            {
                Log.Info("DelayedLogic started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    Thread.Sleep(500);
                    var due = new List<KeyValuePair<DateTime, Action>>();
                    lock (_lock)
                    {
                        while (_actions.Count > 0 && _actions.Keys[0] <= DateTime.Now)
                        {
                            var key = _actions.Keys[0];
                            due.Add(new KeyValuePair<DateTime, Action>(key, _actions[key]));
                            _actions.Remove(key);
                        }
                    }
                    foreach (var kv in due)
                    {
                        try
                        {
                            kv.Value.Invoke();
                        }
                        catch (Exception e)
                        {
                            // the action was already removed: a failing action runs once and is dropped
                            Log.Error("DelayedLogic action failed (dropped)", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("DelayedLogic start Error", e);
            }
        }
    }
}