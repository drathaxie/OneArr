using Microsoft.Extensions.Logging;
using OneArr.Infrastructure.Diagnostics;
using Xunit;

namespace OneArr.Tests;

public class LogBufferTests
{
    [Fact]
    public void Keeps_the_newest_entries_filters_by_level_and_redacts_keys()
    {
        var buffer = new LogBuffer();
        var logger = new BufferLoggerProvider(buffer).CreateLogger("OneArr.Test");
        for (var i = 0; i < LogBuffer.Capacity + 10; i++) logger.LogInformation("info {I}", i);
        logger.LogWarning("Indexer failed: http://x.test/api?apikey=hunter2&t=search");

        var all = buffer.Recent(LogBuffer.Capacity, LogLevel.Information);
        Assert.Equal(LogBuffer.Capacity, all.Count);
        var warning = Assert.Single(buffer.Recent(10, LogLevel.Warning));
        Assert.DoesNotContain("hunter2", warning.Message);
        Assert.Equal($"info {LogBuffer.Capacity + 9}", all[1].Message);
    }
}
