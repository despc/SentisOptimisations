using System;
using System.Diagnostics;

namespace SentisOptimisationsPlugin.Freezer;

/// <summary>
/// The worst frame of the last few seconds, as the game's own CPU load: that frame's time over
/// the 16.7 ms a frame has, in percent - so a 33 ms frame is 200%.
///
/// The average next to it is sampled twice a second and misses what happens in between; this one
/// looks at every frame. It keeps one maximum per second for the last <see cref="Seconds"/>
/// seconds, which costs a comparison a frame and nothing to read.
/// </summary>
public static class CpuLoadPeak
{
    public const int Seconds = 5;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Lock = new();
    private static readonly long[] BucketSecond = new long[Seconds];
    private static readonly bool[] BucketUsed = new bool[Seconds];
    private static readonly float[] BucketMax = new float[Seconds];

    /// <summary>One frame's load, from the game thread.</summary>
    public static void Sample(float load) => Sample(load, Clock.ElapsedMilliseconds);

    /// <summary>The highest load of the last <see cref="Seconds"/> seconds, 0 if nothing was sampled.</summary>
    public static float Peak() => Peak(Clock.ElapsedMilliseconds);

    public static void Sample(float load, long nowMs)
    {
        if (float.IsNaN(load) || float.IsInfinity(load) || nowMs < 0) return;
        var second = nowMs / 1000;
        var i = (int)(second % Seconds);
        lock (Lock)
        {
            if (!BucketUsed[i] || BucketSecond[i] != second)
            {
                BucketUsed[i] = true;
                BucketSecond[i] = second;
                BucketMax[i] = load;
            }
            else if (load > BucketMax[i])
            {
                BucketMax[i] = load;
            }
        }
    }

    public static float Peak(long nowMs)
    {
        var second = nowMs / 1000;
        var peak = 0f;
        lock (Lock)
        {
            for (var i = 0; i < Seconds; i++)
                if (BucketUsed[i] && second - BucketSecond[i] < Seconds && BucketMax[i] > peak)
                    peak = BucketMax[i];
        }
        return (float)Math.Round(peak, 1);
    }

    public static void Reset()
    {
        lock (Lock)
        {
            for (var i = 0; i < Seconds; i++)
            {
                BucketUsed[i] = false;
                BucketMax[i] = 0;
            }
        }
    }
}
