using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace SentisOptimisationsPlugin.ShipTool;

/// <summary>
/// The worker that runs what ship tools hand off the game thread.
///
/// It waits on the queue instead of polling it: the previous loop slept 160 ms whenever the queue
/// was empty, so the first action after a quiet moment waited up to that long before anything
/// happened - on a tool that fires every 167 ms, most of them did.
/// </summary>
public class ShipToolsAsyncQueues
{
    public static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private BlockingCollection<Action> _actions = new BlockingCollection<Action>(new ConcurrentQueue<Action>());

    public CancellationTokenSource CancellationTokenSource { get; set; }

    /// <summary>Actions waiting to run; 0 when the worker keeps up.</summary>
    public int Pending => _actions.Count;

    public void OnLoaded()
    {
        CancellationTokenSource = new CancellationTokenSource();
        if (_actions.IsAddingCompleted) _actions = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
        Task.Run(StartLoop);
    }

    public void EnqueueAction(Action action)
    {
        if (action == null) return;
        try
        {
            _actions.Add(action);
        }
        catch (Exception e) when (e is InvalidOperationException || e is ObjectDisposedException)
        {
            // The world is unloading and the queue is closed; the action is no longer wanted.
        }
    }

    public void OnUnloading()
    {
        CancellationTokenSource?.Cancel();
        try
        {
            _actions.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartLoop()
    {
        var token = CancellationTokenSource.Token;
        try
        {
            Log.Info("Ship Tools loop started");
            foreach (var action in _actions.GetConsumingEnumerable(token))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Ship Tools Async Error");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The world is unloading.
        }
        catch (Exception e)
        {
            Log.Error(e, "Ship Tools Async loop Error");
        }
    }
}
