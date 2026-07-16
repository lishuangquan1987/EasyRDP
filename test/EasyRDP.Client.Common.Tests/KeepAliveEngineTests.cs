using EasyRDP.Client.Common;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class KeepAliveEngineTests
{
    [Fact]
    public void Timeout_ShouldFireEvent()
    {
        // Use very short intervals for testing
        var engine = new KeepAliveEngine(intervalMs: 100, timeoutMs: 300);

        int sendCount = 0;
        bool timedOut = false;
        engine.Timeout += () => timedOut = true;

        engine.Start(() => { sendCount++; return true; });

        // Wait for timeout
        SpinWait.SpinUntil(() => timedOut, 2000);
        engine.Stop();

        Assert.True(timedOut, "Should detect timeout when no acks received");
        Assert.True(sendCount >= 2, "Should have sent multiple keepalives");
    }

    [Fact]
    public void OnAckReceived_ShouldResetTimeout()
    {
        var engine = new KeepAliveEngine(intervalMs: 100, timeoutMs: 2000);

        bool timedOut = false;
        engine.Timeout += () => timedOut = true;

        engine.Start(() => true);

        // Keep resetting the timer by acknowledging
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            engine.OnAckReceived();
            Assert.False(timedOut, $"Should not timeout after ack #{i}");
        }

        engine.Stop();
    }

    [Fact]
    public void Start_ShouldSendImmediately()
    {
        var engine = new KeepAliveEngine(intervalMs: 5000, timeoutMs: 30000);
        bool sent = false;

        engine.Start(() => { sent = true; return true; });
        Thread.Sleep(100); // give thread time to send

        Assert.True(sent);
        engine.Stop();
    }

    [Fact]
    public void Stop_ShouldBeIdempotent()
    {
        var engine = new KeepAliveEngine();
        engine.Start(() => true);
        Thread.Sleep(50);
        engine.Stop();
        engine.Stop(); // should not throw
    }

    [Fact]
    public void Stop_WithoutStart_ShouldNotThrow()
    {
        var engine = new KeepAliveEngine();
        engine.Stop();
    }

    [Fact]
    public void IsTimeout_AfterAck_ShouldBeFalse()
    {
        var engine = new KeepAliveEngine(timeoutMs: 100);
        engine.OnAckReceived();
        Assert.False(engine.IsTimeout);
    }
}
