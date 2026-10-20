using ALMARFELD.SubFlow.Core.Subtitles;

namespace ALMARFELD.SubFlow.Tests;

public class SubtitleFormatTests
{
    [Fact]
    public void Srt_RoundTrip_PreservesTimestampsAndText()
    {
        var doc = SubtitleParser.Parse(TestMedia.EnglishSrt);
        Assert.Equal(SubtitleFormat.Srt, doc.Format);
        Assert.Equal(4, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), doc.Cues[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3250), doc.Cues[1].End);
        Assert.Equal("I was born ready!\nLet's go.", doc.Cues[1].Text);

        var again = SubtitleParser.Parse(SubtitleWriter.Write(doc));
        Assert.Equal(doc.Cues.Select(c => (c.Start, c.End, c.Text)), again.Cues.Select(c => (c.Start, c.End, c.Text)));
    }

    [Fact]
    public void Srt_ToleratesMissingBlankLinesDotSeparatorAndBom()
    {
        var content = "﻿1\r\n00:00:01.5 --> 00:00:02,000\r\nHello\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n";
        var doc = SubtitleParser.Parse(content);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("Hello", doc.Cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), doc.Cues[0].Start);
        Assert.Equal("World", doc.Cues[1].Text);
    }

    [Fact]
    public void Srt_CorruptContent_Throws()
    {
        Assert.Throws<SubtitleParseException>(() => SubtitleParser.Parse("this is not a subtitle file at all", SubtitleFormat.Srt));
    }

    [Fact]
    public void Srt_KeepsUnicodeAndRightToLeftText()
    {
        var content = "1\n00:00:01,000 --> 00:00:02,000\nمرحبا بالعالم\n\n2\n00:00:02,000 --> 00:00:03,000\nȘtiință și țară — 日本語 😀\n";
        var doc = SubtitleParser.Parse(content);
        var written = SubtitleParser.Parse(SubtitleWriter.Write(doc));
        Assert.Equal("مرحبا بالعالم", written.Cues[0].Text);
        Assert.Equal("Știință și țară — 日本語 😀", written.Cues[1].Text);
    }

    [Fact]
    public void Ass_RoundTrip_PreservesHeaderStylesTimingAndPositioning()
    {
        var ass = """
            [Script Info]
            Title: Test
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.23,0:00:02.50,Default,Goku,0,0,0,,{\an8\i1}Hello, there!\NHow are you?
            Comment: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,translator note
            Dialogue: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100{\p0}
            """;
        var doc = SubtitleParser.Parse(ass);
        Assert.Equal(SubtitleFormat.Ass, doc.Format);
        Assert.Equal(3, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1230), doc.Cues[0].Start);
        Assert.False(doc.Cues[0].IsNonDialogue);
        Assert.True(doc.Cues[1].IsNonDialogue); // comment
        Assert.True(doc.Cues[2].IsNonDialogue); // drawing

        var split = SubtitleMarkup.Split(doc.Cues[0].Text, SubtitleFormat.Ass);
        Assert.Equal(@"{\an8\i1}", split.Prefix);
        Assert.Equal("Hello, there!\nHow are you?", split.Plain);

        var translated = doc.WithTexts(new Dictionary<int, string> { [1] = split.Rebuild("Salut!\nCe faci?") });
        var output = SubtitleWriter.Write(translated);
        Assert.Contains("Style: Default,Arial,20", output);
        Assert.Contains(@"Dialogue: 0,0:00:01.23,0:00:02.50,Default,Goku,0,0,0,,{\an8\i1}Salut!\NCe faci?", output);
        Assert.Contains("Comment: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,translator note", output);
    }

    [Fact]
    public void WebVtt_ParsesSettingsAndRoundTrips()
    {
        var vtt = "WEBVTT\n\nSTYLE\n::cue { color: yellow }\n\nintro\n00:01.000 --> 00:02.500 align:start line:10%\n<v Bob>Hello there\n\n00:00:03.000 --> 00:00:04.000\nSecond\n";
        var doc = SubtitleParser.Parse(vtt);
        Assert.Equal(SubtitleFormat.WebVtt, doc.Format);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("intro", doc.Cues[0].VttIdentifier);
        Assert.Equal("align:start line:10%", doc.Cues[0].VttSettings);
        Assert.Equal("Hello there", SubtitleMarkup.ToPlain(doc.Cues[0].Text, SubtitleFormat.WebVtt));
        var again = SubtitleParser.Parse(SubtitleWriter.Write(doc));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), again.Cues[0].End);
        Assert.Contains("::cue", again.VttHeader);
    }

    [Theory]
    [InlineData("<i>Hello</i>", "<i>", "Hello", "</i>")]
    [InlineData("<i>Line one</i>\n<i>Line two</i>", "<i>", "Line one\nLine two", "</i>")]
    [InlineData("{\\an8}Top text", "{\\an8}", "Top text", "")]
    [InlineData("Plain <b>bold</b> words", "", "Plain bold words", "")]
    public void Markup_SplitsWrappersFromTranslatableText(string text, string prefix, string plain, string suffix)
    {
        var split = SubtitleMarkup.Split(text, SubtitleFormat.Srt);
        Assert.Equal(prefix, split.Prefix);
        Assert.Equal(plain, split.Plain);
        Assert.Equal(suffix, split.Suffix);
    }

    [Fact]
    public void ToSrt_ConvertsAssKeepingTimingAndDroppingComments()
    {
        var doc = SubtitleParser.Parse("[Script Info]\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}Hi\nComment: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,note\n");
        var srt = doc.ToSrt();
        Assert.Single(srt.Cues);
        Assert.Equal("<i>Hi</i>", srt.Cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), srt.Cues[0].Start);
    }

    [Fact]
    public void EncodingDetection_ReadsLegacyAnsiFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("legacy.srt");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(path, System.Text.Encoding.GetEncoding(1252).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé déjà vu\r\n"));
        var doc = SubtitleParser.ParseFile(path);
        Assert.Contains("déjà", doc.Cues[0].Text);
    }
}
