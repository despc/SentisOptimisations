using System;
using System.Collections.Concurrent;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// Bookkeeping for the frozen-frames compensation of production blocks.
///
/// On freeze the block's simulation frame is stamped; on unfreeze the frozen period is
/// ACCUMULATED into a per-block pending total instead of being written to the timer directly.
/// The pending total is applied later (deferred apply) and taken atomically. Guarantees:
///  * a frozen period is never lost - a re-freeze before the deferred apply landed ADDS to the
///    pending total instead of overwriting it;
///  * garbage stamps (reused EntityId of a deleted block, stale leftovers) can never produce an
///    absurd delta: a stamp in the future is dropped, deltas are clamped to maxFrames;
///  * the accumulated compensation is taken exactly once (TryTakeCompensation), so a scheduled
///    apply can never apply the same period twice, and an apply that fires while the block is
///    frozen again leaves the pending total intact for the next unfreeze.
/// </summary>
public static class CompensationTracker
{
    // blockId -> simulation frame at which the current freeze started
    static readonly ConcurrentDictionary<long, ulong> FrozenSince = new();

    // blockId -> frames frozen but not yet compensated (accumulated across freeze cycles)
    static readonly ConcurrentDictionary<long, uint> PendingFrames = new();

    // blockId -> a deferred apply is already scheduled and will take the accumulated total
    static readonly ConcurrentDictionary<long, byte> ApplyScheduled = new();

    // Set only while a compensation fast-forward pass runs on the game thread. The AddItems
    // overflow handling must not touch ordinary item flow - only the burst produced here.
    [ThreadStatic] private static bool _inCompensation;
    public static bool InCompensation(long blockId = 0) => _inCompensation;

    /// <summary>Runs a compensation fast-forward pass; inside it InCompensation() is true.</summary>
    public static void RunCompensationPass(Action pass) => RunCompensationPass(uint.MaxValue, pass);

    /// <summary>
    /// Runs a fast-forward pass. InCompensation() is only true when the delta is big enough to
    /// be a real catch-up (more than one second) - a normal one-frame tick must not activate the
    /// overflow handling.
    /// </summary>
    public static void RunCompensationPass(uint framesFromLastTrigger, Action pass)
    {
        var active = framesFromLastTrigger > 60;
        _inCompensation = active;
        try
        {
            pass();
        }
        finally
        {
            _inCompensation = false;
        }
    }

    /// <summary>Called on the game thread at the moment the grid is frozen.</summary>
    public static void OnFrozen(long blockId, ulong frame)
    {
        FrozenSince[blockId] = frame;
    }

    /// <summary>True while a freeze stamp exists for the block (it is currently frozen).</summary>
    public static bool IsFrozen(long blockId) => FrozenSince.ContainsKey(blockId);

    /// <summary>
    /// Called when the block unfreezes. Accumulates the frozen period into the pending total.
    /// Returns true when there is uncompensated work pending (the caller should schedule an apply
    /// via TryScheduleApply). A stamp that lies in the future (stale entry from a deleted block
    /// whose EntityId was reused) is dropped instead of producing a wrapped-around delta.
    /// </summary>
    public static bool OnUnfrozen(long blockId, ulong frame, ulong maxFrames)
    {
        if (FrozenSince.TryRemove(blockId, out var since))
        {
            if (since > frame)
            {
                SentisOptimisationsPlugin.Log.Warn(
                    $"Compensation: stale future stamp for block {blockId} (stamped {since}, now {frame}) - skipped");
                PendingFrames.TryRemove(blockId, out _);
                return false;
            }

            var delta = frame - since;
            if (delta > maxFrames)
            {
                SentisOptimisationsPlugin.Log.Warn(
                    $"Compensation: delta {delta} frames for block {blockId} exceeds sanity cap {maxFrames} - clamped");
                delta = maxFrames;
            }

            var cappedDelta = (uint)Math.Min(delta, uint.MaxValue);
            PendingFrames.AddOrUpdate(
                blockId,
                cappedDelta,
                (_, pending) => (uint)Math.Min(maxFrames, (ulong)pending + delta));
        }

        return PendingFrames.ContainsKey(blockId);
    }

    /// <summary>
    /// Marks that a deferred apply has been scheduled for the block. Returns false when one is
    /// already scheduled - the existing apply will pick up everything accumulated so far.
    /// </summary>
    public static bool TryScheduleApply(long blockId) => ApplyScheduled.TryAdd(blockId, 0);

    /// <summary>
    /// Clears the schedule marker (the deferred apply fired but did not take the compensation -
    /// e.g. the block was frozen again - or it threw). The next unfreeze can schedule a new apply.
    /// </summary>
    public static void ReleaseSchedule(long blockId) => ApplyScheduled.TryRemove(blockId, out _);

    /// <summary>Atomically takes the whole accumulated compensation. Succeeds at most once per
    /// accumulation - a second caller gets nothing.</summary>
    public static bool TryTakeCompensation(long blockId, out uint frames)
    {
        ApplyScheduled.TryRemove(blockId, out _);
        frames = 0;
        return PendingFrames.TryRemove(blockId, out frames) && frames > 0;
    }

    /// <summary>Current uncompensated total (diagnostics/tests).</summary>
    public static uint? PeekPending(long blockId) =>
        PendingFrames.TryGetValue(blockId, out var v) ? v : (uint?)null;

    /// <summary>
    /// Drops all compensation state for a block. MUST be called when the block leaves the world:
    /// EntityIds are reused after deletion and a leftover "future" stamp would otherwise poison
    /// the next holder of that id.
    /// </summary>
    public static void Forget(long blockId)
    {
        FrozenSince.TryRemove(blockId, out _);
        PendingFrames.TryRemove(blockId, out _);
        ApplyScheduled.TryRemove(blockId, out _);
    }

    public static void ForgetBlocks(System.Collections.Generic.IEnumerable<long> blockIds)
    {
        foreach (var id in blockIds)
            Forget(id);
    }
}
