using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Tests;

public class ToolLocatorTests
{
    [Theory]
    [InlineData("ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024 the FFmpeg developers", "7.1")]
    [InlineData("ffprobe version n6.1.1 Copyright (c) 2007-2023", "n6.1.1")]
    [InlineData("mkvmerge v88.0 ('All I Know') 64-bit", "88.0")]
    [InlineData("something else", "something else")]
    public void ShortVersion_KeepsOnlyTheVersionNumber(string line, string expected) =>
        Assert.Equal(expected, ToolLocator.ShortVersion(line));
}
