namespace Paradise.Hosting.Test;

public class HostOptionsTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("soon")]
    [Arguments("-30")]
    [Arguments("0")]
    [Arguments("2.5")]
    [Arguments("NaN")]
    public async Task ShutdownBudgetStaysFiveSecondsUnlessTheGpuTimeoutIsLonger(string? value)
    {
        await Assert.That(HostOptions.DefaultShutdownTimeout(value)).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ShutdownBudgetGrowsWithALongerGpuTimeout()
    {
        await Assert.That(HostOptions.DefaultShutdownTimeout("60")).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(HostOptions.DefaultShutdownTimeout("7.5")).IsEqualTo(TimeSpan.FromSeconds(7.5));
    }

    [Test]
    public async Task ShutdownBudgetIsClampedToWhatTheHostAccepts()
    {
        var budget = HostOptions.DefaultShutdownTimeout("1e12");
        await Assert.That(budget).IsEqualTo(TimeSpan.FromMilliseconds(int.MaxValue));
        ParadiseHost.Validate(new HostOptions { ShutdownTimeout = budget });
    }
}
