using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace SentisOptimisationsPlugin
{
    public class SendReplicablesAsync
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Queue<AsyncSync.ISendToClientWrapper> _queue = new Queue<AsyncSync.ISendToClientWrapper>(2048);
        private static DateTime _lastErrorLogged = DateTime.MinValue;
       
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public void OnLoaded()
        {
            CancellationTokenSource = new CancellationTokenSource();
            Task.Run(SendToClient);
        }

        public void OnUnloading()
        {
            CancellationTokenSource.Cancel();
        }

        public void SendToClient()
        {
            try
            {
                Log.Info("Send to client loop started");
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        Thread.Sleep(1);
                        AsyncSync.ISendToClientWrapper dequeue = null;
                        lock (_queue)
                        {
                            while (_queue.Count > 0 && dequeue == null)
                            {
                                dequeue = _queue.Dequeue();
                            }
                        }
                        
                        if (dequeue == null)
                        {
                            continue;
                        }
                        dequeue.DoSendToClient();
                    }
                    catch (Exception e)
                    {
                        // throttled: this loop runs every ~1 ms; one line per 5 s is enough
                        var now = DateTime.Now;
                        if (now - _lastErrorLogged > TimeSpan.FromSeconds(5))
                        {
                            _lastErrorLogged = now;
                            Log.Error("Send to client loop Error (further errors throttled for 5 s)", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Send to client loop Error", e);
            }
        }        

    }
}