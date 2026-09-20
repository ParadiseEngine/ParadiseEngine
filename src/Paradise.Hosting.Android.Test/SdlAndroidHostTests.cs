using Microsoft.Extensions.Logging;
using Paradise.Hosting.Android;
using TUnit.Assertions;
using TUnit.Core;

namespace Paradise.Hosting.Android.Test;

public sealed class SdlAndroidHostTests
{
    [Test]
    public async Task passes_logger_and_returns_success()
    {
        var logger = new TestLogger();
        ILogger? actual = null;
        var exit = SdlAndroidHost.Run(value => actual = value, logger);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ReferenceEquals(actual, logger)).IsTrue();
        await Assert.That(logger.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task application_failure_is_logged_and_returned()
    {
        var logger = new TestLogger();
        var failure = new InvalidOperationException("test application failure");
        var exit = SdlAndroidHost.Run(_ => throw failure, logger);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(logger.Calls).IsEqualTo(1);
        await Assert.That(logger.Level).IsEqualTo(LogLevel.Critical);
        await Assert.That(ReferenceEquals(logger.Exception, failure)).IsTrue();
    }

    [Test]
    public async Task diagnostic_failure_does_not_escape_native_boundary()
    {
        var logger = new TestLogger { ThrowOnLog = true };
        var exit = SdlAndroidHost.Run(_ => throw new InvalidOperationException("application"), logger);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(logger.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task rejects_null_application()
    {
        await Assert.That(() => SdlAndroidHost.Run(null!, new TestLogger())).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task rejects_null_logger()
    {
        await Assert.That(() => SdlAndroidHost.Run(_ => { }, null!)).Throws<ArgumentNullException>();
    }

    private sealed class TestLogger : ILogger
    {
        public int Calls { get; private set; }
        public LogLevel Level { get; private set; }
        public Exception? Exception { get; private set; }
        public bool ThrowOnLog { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            Level = logLevel;
            Exception = exception;
            if (ThrowOnLog) throw new InvalidOperationException("test logger failure");
        }
    }
}
