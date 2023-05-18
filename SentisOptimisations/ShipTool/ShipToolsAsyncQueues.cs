using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace SentisOptimisationsPlugin.ShipTool;

public class ShipToolsAsyncQueues
{
    public static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public Queue<Action> AsynActions = new Queue<Action>();

    public CancellationTokenSource CancellationTokenSource { get; set; }

    public void OnLoaded()
    {
        CancellationTokenSource = new CancellationTokenSource();
        Task.Run(StartLoop);
    }

    public void EnqueueAction(Action action)
    {
        AsynActions.Enqueue(action);
    }

    public void OnUnloading()
    {
        CancellationTokenSource.Cancel();
    }

    private void StartLoop()
    {
        try
        {
            Log.Info("Ship Tools loop started");
            while (!CancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (AsynActions.Count == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    Action dequeue = null;

                    while (AsynActions.Count > 0 && dequeue == null)
                    {
                        dequeue = AsynActions.Dequeue();
                    }

                    if (dequeue == null)
                    {
                        continue;
                    }

                    dequeue.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error("Ship Tools Async Error", e);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Ship Tools Async loop Error", e);
        }
    }
}