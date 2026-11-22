using System.Text;
using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class MiningSkillsStartupBoundaryTests
{
    [Fact]
    public void EmptyFile_BoundaryIsZero()
    {
        using var source = new TemporarySkillsSource();

        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        Assert.Equal(0, boundary.Position);
    }

    [Fact]
    public void CompleteFinalLine_BoundaryEqualsEof()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));

        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        Assert.Equal(new FileInfo(source.Path).Length, boundary.Position);
    }

    [Fact]
    public void PartialFinalLine_BoundaryStartsBeforePartialRecord()
    {
        using var source = new TemporarySkillsSource();
        var complete = MiningLine(80m);
        source.Write(complete + "[10:01:00] Mining increased by 0,001000 to 80,0");

        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        Assert.Equal(Utf8.GetByteCount(complete), boundary.Position);
    }

    [Fact]
    public void NoNewline_BoundaryIsZero()
    {
        using var source = new TemporarySkillsSource();
        source.Write("[10:00:00] Mining increased by 0,001000 to 80,001000");

        Assert.Equal(0, new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary().Position);
    }

    [Fact]
    public void Bootstrap_FindsLatestMiningBeforeBoundary()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m) + MiningLine(80.001m));
        var reader = new MiningSkillsSourceReader(source.Path);
        var boundary = reader.CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80.001m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_FirstMiningLineWithUtf8Bom_IsFound()
    {
        using var source = new TemporarySkillsSource();
        source.WriteBytes(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8.GetBytes(MiningLine(80m))).ToArray());
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_BomMiningOnlyRecord_ReturnsCurrent()
    {
        using var source = new TemporarySkillsSource();
        source.WriteBytes(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8.GetBytes(MiningLine(80.001m))).ToArray());
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(80.001m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_BomBeforeNonMiningThenLaterMining_FindsLatestMining()
    {
        using var source = new TemporarySkillsSource();
        source.WriteBytes(new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Utf8.GetBytes(SkillLine("Prospecting", 50m) + MiningLine(80.002m)))
            .ToArray());
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(80.002m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_WithoutBom_StillFindsMining()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80.003m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(80.003m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_IgnoresOtherSkills()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m) + SkillLine("Prospecting", 50m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_IgnoresMalformedMiningLines()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m) + "[10:01:00] Mining increased by invalid to 80,001000\n");
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80m, result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_ReturnsNotFoundWhenMiningAbsent()
    {
        using var source = new TemporarySkillsSource();
        source.Write(SkillLine("Prospecting", 50m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.NotFound, result.Status);
        Assert.Null(result.CurrentValue);
    }

    [Fact]
    public void Bootstrap_DoesNotReadMiningAppendedAfterBoundary()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));
        var reader = new MiningSkillsSourceReader(source.Path);
        var boundary = reader.CaptureSessionBoundary();
        source.Append(MiningLine(80.001m));

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80m, result.CurrentValue);
    }

    [Fact]
    public void Reader_ReadsPostBoundaryAppendExactlyOnce()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));
        var reader = new MiningSkillsSourceReader(source.Path);
        _ = reader.CaptureSessionBoundary();
        var appended = MiningLine(80.001m);
        source.Append(appended);

        Assert.Equal([appended.TrimEnd('\n')], reader.ReadNewLines().Lines);
        Assert.Empty(reader.ReadNewLines().Lines);
    }

    [Fact]
    public void PartialLineAtCapture_IsCompletedAndReturnedLiveExactlyOnce()
    {
        using var source = new TemporarySkillsSource();
        var historical = MiningLine(80m);
        var partial = "[10:01:00] Mining increased by 0,001000 to 80,0";
        source.Write(historical + partial);
        var reader = new MiningSkillsSourceReader(source.Path);
        var boundary = reader.CaptureSessionBoundary();

        var bootstrap = new MiningCurrentBootstrapper().Find(source.Path, boundary);
        source.Append("01000\n");

        Assert.Equal(80m, bootstrap.CurrentValue);
        Assert.Equal([partial + "01000"], reader.ReadNewLines().Lines);
        Assert.Empty(reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReverseScan_HandlesLineAcrossBlockBoundary()
    {
        using var source = new TemporarySkillsSource();
        var mining = MiningLine(80.001m);
        var prefix = new string('x', 4079) + "\n";
        var suffix = new string('y', 8192 - Utf8.GetByteCount(prefix) - Utf8.GetByteCount(mining) - 1) + "\n";
        source.Write(prefix + mining + suffix);
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80.001m, result.CurrentValue);
    }

    [Fact]
    public void CRLF_IsHandled()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80.001m).Replace("\n", "\r\n", StringComparison.Ordinal));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80.001m, result.CurrentValue);
    }

    [Fact]
    public void TruncatedBeforeBootstrap_IsReportedAsContinuityLoss()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();
        source.Write("short\n");

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.ContinuityLost, result.Status);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Truncated, result.ContinuityLossReason);
    }

    [Fact]
    public void ReplacedBeforeBootstrap_IsReportedAsContinuityLoss()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();
        source.Write(MiningLine(81m));

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.ContinuityLost, result.Status);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, result.ContinuityLossReason);
    }

    [Fact]
    public void AppendAfterBoundary_DoesNotCauseContinuityLoss()
    {
        using var source = new TemporarySkillsSource();
        source.Write(MiningLine(80m));
        var boundary = new MiningSkillsSourceReader(source.Path).CaptureSessionBoundary();
        source.Append(SkillLine("Prospecting", 50m));

        var result = new MiningCurrentBootstrapper().Find(source.Path, boundary);

        Assert.Equal(MiningCurrentBootstrapStatus.Found, result.Status);
        Assert.Equal(80m, result.CurrentValue);
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static string MiningLine(decimal currentValue) => SkillLine("Mining", currentValue);

    private static string SkillLine(string skillName, decimal currentValue) =>
        $"[10:00:00] {skillName} increased by 0,001000 to {currentValue:0.000000}\n".Replace('.', ',');

    private sealed class TemporarySkillsSource : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");

        public TemporarySkillsSource()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "_Skills.2026-10.txt");
            File.WriteAllText(Path, string.Empty, Utf8);
        }

        public string Path { get; }

        public void Write(string text) => File.WriteAllText(Path, text, Utf8);

        public void WriteBytes(byte[] bytes) => File.WriteAllBytes(Path, bytes);

        public void Append(string text) => File.AppendAllText(Path, text, Utf8);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
