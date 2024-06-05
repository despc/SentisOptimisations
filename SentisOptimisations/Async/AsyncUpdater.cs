using System;
using System.Threading;
using System.Threading.Tasks;

namespace SentisOptimisationsPlugin.Async;

public class AsyncUpdater
{
    public static DistributedUpdater DistributedUpdaterAfter10 = new DistributedUpdater(10);
    public static DistributedUpdater DistributedUpdaterAfter100 = new DistributedUpdater(100);
    public static DistributedUpdater DistributedUpdaterAfter30 = new DistributedUpdater(30);
    public CancellationTokenSource CancellationTokenSource { get; set; }

    public void OnLoaded()
    {
        CancellationTokenSource = new CancellationTokenSource();
        Task.Run(AsyncUpdateLoop);
        DistributedUpdaterAfter10.WrappersAfter.AddWrapper(new MyThrustUpdateEntityWrapper10());
        
        DistributedUpdaterAfter30.WrappersAfter.AddWrapper(new MyTextPanelWrapper30());
        
        DistributedUpdaterAfter100.WrappersAfter.AddWrapper(new MyThrustUpdateEntityWrapper100());
    }

    public void OnUnloading()
    {
        CancellationTokenSource.Cancel();
    }
    
    public async void AsyncUpdateLoop()
    {
        try
        {
            SentisOptimisationsPlugin.Log.Info("Async Update loop started");
            while (!CancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (!SentisOptimisationsPlugin.Config.AsyncLogicUpdateMain)
                    {
                        await Task.Delay(1000);
                    }
                    await Task.Delay(16);
                    DistributedUpdaterAfter10.Update();
                    DistributedUpdaterAfter30.Update();
                    DistributedUpdaterAfter100.Update();
                }
                catch (Exception e)
                {
                    SentisOptimisationsPlugin.Log.Error("Async Update loop Error", e);
                }
            }
        }
        catch (Exception e)
        {
            SentisOptimisationsPlugin.Log.Error("CheckLoop start Error", e);
        }
    }
}