using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// v1.2.113 (2.3/2.4) — the re-armed strip-bug evidence collector must be flood-proof:
// hard 5 lines/second with honest dropped-line accounting, and a 1 MB file cap that
// truncates (the useful window is the last few minutes before a strip, never history).
public sealed class WidgetSizeLogTests
{
    [Fact]
    public void RateLimiter_AllowsFivePerSecond_CountsTheRest()
    {
        var limiter = new WidgetSizeLogRateLimiter();
        DateTimeOffset t0 = new(2026, 07, 14, 10, 0, 0, TimeSpan.Zero);

        int allowed = 0;
        long observedDropped = 0;
        for (int i = 0; i < 100; i++)
        {
            if (limiter.TryAcquire(t0.AddMilliseconds(i), out long dropped))
            {
                allowed++;
                observedDropped += dropped;
            }
        }

        Assert.Equal(WidgetSizeLogRateLimiter.MaxLinesPerWindow, allowed);
        Assert.Equal(0, observedDropped); // drops are reported on the NEXT allowed line

        // The next second's first allowed line carries the 95 dropped lines.
        Assert.True(limiter.TryAcquire(t0.AddSeconds(1.1), out long reported));
        Assert.Equal(95, reported);
    }

    [Fact]
    public void RateLimiter_WindowResets_EachSecond()
    {
        var limiter = new WidgetSizeLogRateLimiter();
        DateTimeOffset t0 = new(2026, 07, 14, 10, 0, 0, TimeSpan.Zero);

        for (int second = 0; second < 3; second++)
        {
            int allowedThisSecond = 0;
            for (int i = 0; i < 10; i++)
            {
                if (limiter.TryAcquire(t0.AddSeconds(second).AddMilliseconds(i * 50), out _))
                {
                    allowedThisSecond++;
                }
            }

            Assert.Equal(WidgetSizeLogRateLimiter.MaxLinesPerWindow, allowedThisSecond);
        }
    }

    [Fact]
    public void Append_TruncatesWhenOverCap()
    {
        string path = Path.Combine(Path.GetTempPath(), $"widget-size-{Guid.NewGuid():N}.log");
        try
        {
            // Grow past a tiny cap, then confirm the next append starts fresh.
            WidgetSizeLog.Append(new string('x', 300), path, maxBytes: 200);
            long grownLength = new FileInfo(path).Length;
            Assert.True(grownLength > 200);

            WidgetSizeLog.Append("after-cap", path, maxBytes: 200);
            string content = File.ReadAllText(path);
            Assert.DoesNotContain("xxx", content);
            Assert.Contains("after-cap", content);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Append_SurvivesUnwritablePath()
    {
        // Diagnostics must never hurt the widget: an impossible path is swallowed.
        Exception? ex = Record.Exception(() =>
            WidgetSizeLog.Append("line", "Z:\\::invalid::\\widget-size.log"));
        Assert.Null(ex);
    }
}
