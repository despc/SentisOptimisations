using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.WorldEnvironment;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;

namespace SentisOptimisationsPlugin
{
    public class SendReplicablesAsync
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static Queue<AsyncSync.ISendToClientWrapper> _queue = new Queue<AsyncSync.ISendToClientWrapper>();
       
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
                        if (_queue.Count == 0)
                        {
                            continue;
                        }

                        AsyncSync.ISendToClientWrapper dequeue = null;

                        while (_queue.Count > 0 && dequeue == null)
                        {
                            dequeue = _queue.Dequeue();
                        }
                        
                        if (dequeue == null)
                        {
                            return;
                        }
                        dequeue.DoSendToClient();
                    }
                    catch (Exception e)
                    {
                        Log.Error("Send to client loop Error", e);
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