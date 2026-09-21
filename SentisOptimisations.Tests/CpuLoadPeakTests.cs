using SentisOptimisationsPlugin.Freezer;
using Xunit;

namespace SentisOptimisations.Tests
{
    /// <summary>The worst frame of the last five seconds, next to the average CPU load.</summary>
    [Collection("CpuLoadPeak")]
    public class CpuLoadPeakTests
    {
        public CpuLoadPeakTests() => CpuLoadPeak.Reset();

        [Fact]
        public void Nothing_sampled_is_zero() => Assert.Equal(0f, CpuLoadPeak.Peak(10_000));

        [Fact]
        public void The_worst_frame_of_the_window_is_the_peak()
        {
            CpuLoadPeak.Sample(40, 10_000);
            CpuLoadPeak.Sample(230, 11_500);
            CpuLoadPeak.Sample(60, 13_900);
            Assert.Equal(230f, CpuLoadPeak.Peak(14_000));
        }

        [Fact]
        public void A_peak_older_than_five_seconds_is_forgotten()
        {
            CpuLoadPeak.Sample(300, 10_000);
            Assert.Equal(300f, CpuLoadPeak.Peak(14_999));
            Assert.Equal(0f, CpuLoadPeak.Peak(15_000));
            CpuLoadPeak.Sample(50, 15_200);
            Assert.Equal(50f, CpuLoadPeak.Peak(15_200));
        }

        [Fact]
        public void A_second_used_again_later_starts_from_its_own_frames()
        {
            // the same slot of the ring five seconds on must not keep the old maximum
            CpuLoadPeak.Sample(500, 10_100);
            CpuLoadPeak.Sample(20, 15_100);
            Assert.Equal(20f, CpuLoadPeak.Peak(15_100));
        }

        [Fact]
        public void Nonsense_readings_are_ignored()
        {
            CpuLoadPeak.Sample(float.NaN, 10_000);
            CpuLoadPeak.Sample(float.PositiveInfinity, 10_000);
            CpuLoadPeak.Sample(35, 10_000);
            Assert.Equal(35f, CpuLoadPeak.Peak(10_500));
        }
    }
}
