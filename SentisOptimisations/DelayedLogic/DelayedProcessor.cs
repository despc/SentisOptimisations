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
                    try
                    {
                        Thread.Sleep(500);
                        lock (_lock)
                        {
                            if (_actions.Count == 0)
                            {
                                continue;
                            }

                            var firstElement = _actions.Keys[0];
                            if (firstElement > DateTime.Now)
                            {
                                continue;
                            }

                            while (firstElement < DateTime.Now)
                            {
                                _actions[firstElement].Invoke();
                                _actions.Remove(firstElement);
                                if (_actions.Count == 0)
                                {
                                    break;
                                }

                                firstElement = _actions.Keys[0];
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("DelayedLogic Error", e);
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