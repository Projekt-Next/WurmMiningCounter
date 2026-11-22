using System.Text;
using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class MiningSkillsSourceReaderTests
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Constructor_AcceptsMonthlySourceFileName()
    {
        using var source = new TemporarySkillsSource("_Skills.2026-09.txt");

        var reader = new MiningSkillsSourceReader(source.Path);

        Assert.Equal(Path.GetFullPath(source.Path), reader.CanonicalSourcePath);
    }

    [Fact]
    public void Constructor_AcceptsDailySourceFileName()
    {
        using var source = new TemporarySkillsSource("_Skills.2026-09-12.txt");

        var reader = new MiningSkillsSourceReader(source.Path);

        Assert.Equal(Path.GetFullPath(source.Path), reader.CanonicalSourcePath);
    }

    [Theory]
    [InlineData("Skills.2026-09.txt")]
    [InlineData("_Skills.2026-9.txt")]
    [InlineData("_Skills.2026-09-1.txt")]
    [InlineData("_Event.2026-09.txt")]
    public void Constructor_RejectsInvalidSourceFileName(string fileName)
    {
        using var source = new TemporarySkillsSource(fileName);

        Assert.Throws<ArgumentException>(() => new MiningSkillsSourceReader(source.Path));
    }

    [Fact]
    public void CaptureSessionBoundary_DoesNotReplayExistingCompleteLines()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("Historical one\nHistorical two\n");
        var reader = new MiningSkillsSourceReader(source.Path);

        reader.CaptureSessionBoundary();

        Assert.Empty(reader.ReadNewLines().Lines);
    }

    [Fact]
    public void CaptureSessionBoundary_RejectsSecondCaptureWithoutChangingTheOriginalBaseline()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("Historical\n");
        var reader = new MiningSkillsSourceReader(source.Path);
        reader.CaptureSessionBoundary();

        Assert.Throws<InvalidOperationException>(() => reader.CaptureSessionBoundary());

        source.AppendText("New after original baseline\n");
        Assert.Equal(["New after original baseline"], reader.ReadNewLines().Lines);
    }

    [Fact]
    public void CaptureSessionBoundary_RejectsSecondCaptureBeforeOpeningDeletedSource()
    {
        using var source = new TemporarySkillsSource();
        var reader = new MiningSkillsSourceReader(source.Path);
        reader.CaptureSessionBoundary();
        File.Delete(source.Path);

        Assert.Throws<InvalidOperationException>(() => reader.CaptureSessionBoundary());
    }

    [Fact]
    public void CaptureStartOfFileBaseline_RejectsSecondCaptureBeforeOpeningDeletedSource()
    {
        using var source = new TemporarySkillsSource();
        var reader = new MiningSkillsSourceReader(source.Path);
        reader.CaptureStartOfFileBaseline();
        File.Delete(source.Path);

        Assert.Throws<InvalidOperationException>(() => reader.CaptureStartOfFileBaseline());
    }

    [Fact]
    public void ReadNewLines_ReturnsOneNewCompleteLineExactlyOnce()
    {
        using var source = StartedSource();
        source.AppendText("New line\n");

        Assert.Equal(["New line"], source.Reader.ReadNewLines().Lines);
        Assert.Empty(source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_PreservesOrderForSeveralNewLines()
    {
        using var source = StartedSource();
        source.AppendText("First\nSecond\nThird\n");

        Assert.Equal(["First", "Second", "Third"], source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_AtEofWithoutNewDataReturnsEmptyResult()
    {
        using var source = StartedSource();

        var result = source.Reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.False(result.HasContinuityLoss);
    }

    [Fact]
    public void ReadNewLines_DoesNotEmitPartialLineUntilItIsCompleted()
    {
        using var source = StartedSource();
        source.AppendText("Partial");

        Assert.Empty(source.Reader.ReadNewLines().Lines);

        source.AppendText(" line\n");
        Assert.Equal(["Partial line"], source.Reader.ReadNewLines().Lines);
        Assert.Empty(source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_RetriesAnIncompleteUtf8SequenceWithoutCorruption()
    {
        using var source = StartedSource();
        var completeBytes = Utf8WithoutBom.GetBytes("Górnictwo\n");
        source.AppendBytes(completeBytes[..2]);

        Assert.Empty(source.Reader.ReadNewLines().Lines);

        source.AppendBytes(completeBytes[2..]);
        Assert.Equal(["Górnictwo"], source.Reader.ReadNewLines().Lines);
        Assert.Empty(source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_SupportsLf()
    {
        using var source = StartedSource();
        source.AppendText("LF line\n");

        Assert.Equal(["LF line"], source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_SupportsCrLf()
    {
        using var source = StartedSource();
        source.AppendText("CRLF line\r\n");

        Assert.Equal(["CRLF line"], source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_DoesNotEmitCrLfLineUntilTheLfArrives()
    {
        using var source = StartedSource();
        source.AppendText("Split CRLF line\r");

        Assert.Empty(source.Reader.ReadNewLines().Lines);

        source.AppendText("\n");
        Assert.Equal(["Split CRLF line"], source.Reader.ReadNewLines().Lines);
        Assert.Empty(source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_RetriesSeveralIncompleteUtf8ReadsUntilTheLineIsComplete()
    {
        using var source = StartedSource();
        var completeBytes = Utf8WithoutBom.GetBytes("A€\n");
        source.AppendBytes(completeBytes[..2]);

        Assert.Empty(source.Reader.ReadNewLines().Lines);

        source.AppendBytes(completeBytes[2..3]);
        Assert.Empty(source.Reader.ReadNewLines().Lines);

        source.AppendBytes(completeBytes[3..]);
        Assert.Equal(["A€"], source.Reader.ReadNewLines().Lines);
        Assert.Empty(source.Reader.ReadNewLines().Lines);
    }

    [Fact]
    public void ReadNewLines_PendingRecordAtByteLimitCompletesWhenTerminatorArrivesLater()
    {
        using var source = StartedSource();
        source.AppendText(new string('x', MiningSkillsSourceReader.MaximumRecordBytes));

        Assert.Empty(source.Reader.ReadNewLines().Lines);
        Assert.Equal(MiningSkillsSourceReader.MaximumRecordBytes, source.Reader.PendingByteCountForTest);

        source.AppendText("\n");
        var result = source.Reader.ReadNewLines();

        Assert.False(result.HasContinuityLoss);
        Assert.Equal([new string('x', MiningSkillsSourceReader.MaximumRecordBytes)], result.Lines);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);
    }

    [Fact]
    public void ReadNewLines_PendingRecordOneByteOverLimitFailsClosedAndReleasesTheBuffer()
    {
        using var source = StartedSource();
        source.AppendText(new string('x', MiningSkillsSourceReader.MaximumRecordBytes + 1));

        Assert.Empty(source.Reader.ReadNewLines().Lines);
        Assert.Equal(MiningSkillsSourceReader.MaximumRecordBytes, source.Reader.PendingByteCountForTest);

        var result = source.Reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.OversizedRecord, result.ContinuityLossReason);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);

        source.AppendText(new string('y', MiningSkillsSourceReader.MaximumRecordBytes * 2));
        Assert.Equal(MiningSkillsSourceContinuityLossReason.OversizedRecord, source.Reader.ReadNewLines().ContinuityLossReason);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);
    }

    [Fact]
    public void ReadNewLines_PendingRecordGrowthAcrossManyPollsFailsAtTheFirstByteOverLimit()
    {
        using var source = StartedSource();
        var chunk = new string('x', MiningSkillsSourceReader.MaximumRecordBytes / 8);

        for (var poll = 1; poll <= 8; poll++)
        {
            source.AppendText(chunk);
            var result = source.Reader.ReadNewLines();
            Assert.Empty(result.Lines);
            Assert.False(result.HasContinuityLoss);
            Assert.Equal(chunk.Length * poll, source.Reader.PendingByteCountForTest);
        }

        source.AppendText("x");
        var oversized = source.Reader.ReadNewLines();

        Assert.Equal(MiningSkillsSourceContinuityLossReason.OversizedRecord, oversized.ContinuityLossReason);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);
    }

    [Fact]
    public void ReadNewLines_LargeAppendOfCompleteShortLinesIsConsumedInBoundedChunksWithoutLoss()
    {
        using var source = StartedSource();
        const int lineCount = 40_000;
        source.AppendText(string.Concat(Enumerable.Range(0, lineCount).Select(index => $"L{index}\n")));

        var lines = new List<string>();
        MiningSkillsSourceReadResult result;
        do
        {
            result = source.Reader.ReadNewLines();
            Assert.False(result.HasContinuityLoss);
            lines.AddRange(result.Lines);
        } while (result.Lines.Count != 0);

        Assert.Equal(lineCount, lines.Count);
        Assert.Equal("L0", lines[0]);
        Assert.Equal($"L{lineCount - 1}", lines[^1]);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);
    }

    [Fact]
    public void ReadNewLines_MultibyteUtf8AndCrLfAcrossReadBoundaryRemainOneValidRecord()
    {
        using var source = StartedSource();
        var prefix = new string('x', MiningSkillsSourceReader.MaximumRecordBytes - 2);
        source.AppendText(prefix + "ó\r\n");

        Assert.Empty(source.Reader.ReadNewLines().Lines);
        Assert.Equal(MiningSkillsSourceReader.MaximumRecordBytes, source.Reader.PendingByteCountForTest);

        var result = source.Reader.ReadNewLines();

        Assert.False(result.HasContinuityLoss);
        Assert.Equal([prefix + "ó"], result.Lines);
        Assert.Equal(0, source.Reader.PendingByteCountForTest);
    }

    [Fact]
    public void ReadNewLines_DetectsTruncationAsContinuityLoss()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("Historical line long enough\n");
        var reader = source.CreateReaderAndCaptureBoundary();
        source.WriteText("New\n");

        var result = reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Truncated, result.ContinuityLossReason);
    }

    [Fact]
    public void ReadNewLines_DetectsFingerprintMismatchAsReplacement()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("Original source content\n");
        var reader = source.CreateReaderAndCaptureBoundary();
        source.WriteText("Replacement source content that is longer\n");

        var result = reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, result.ContinuityLossReason);
    }

    [Fact]
    public void ReadNewLines_DetectsSameLengthReplacementAsContinuityLoss()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("AAAAAAAAAAAA\n");
        var reader = source.CreateReaderAndCaptureBoundary();
        source.WriteText("BBBBBBBBBBBB\n");

        var result = reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, result.ContinuityLossReason);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, reader.ReadNewLines().ContinuityLossReason);
    }

    [Fact]
    public void ReadNewLines_AfterContinuityLossDoesNotRebaselineOrReadTheNewStream()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("Original source content\n");
        var reader = source.CreateReaderAndCaptureBoundary();
        source.WriteText("Replacement source content that is longer\n");

        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, reader.ReadNewLines().ContinuityLossReason);
        source.AppendText("Must not be emitted\n");

        var result = reader.ReadNewLines();
        Assert.Empty(result.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, result.ContinuityLossReason);
    }

    [Fact]
    public void ReadNewLines_DetectsReplacementAfterSuccessfulReadsWithoutReplayingTheNewStream()
    {
        using var source = new TemporarySkillsSource();
        source.WriteText("AAAA\n");
        var reader = source.CreateReaderAndCaptureBoundary();
        source.AppendText("BBBB\n");

        Assert.Equal(["BBBB"], reader.ReadNewLines().Lines);

        source.WriteText("CCCC\nBBBB\n");
        var loss = reader.ReadNewLines();
        Assert.Empty(loss.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, loss.ContinuityLossReason);

        source.AppendText("Must not be emitted\n");
        var afterLoss = reader.ReadNewLines();
        Assert.Empty(afterLoss.Lines);
        Assert.Equal(MiningSkillsSourceContinuityLossReason.Replaced, afterLoss.ContinuityLossReason);
    }

    private static StartedSkillsSource StartedSource()
    {
        var source = new TemporarySkillsSource();
        var reader = source.CreateReaderAndCaptureBoundary();
        return new StartedSkillsSource(source, reader);
    }

    private sealed class StartedSkillsSource : IDisposable
    {
        public StartedSkillsSource(TemporarySkillsSource source, MiningSkillsSourceReader reader)
        {
            Source = source;
            Reader = reader;
        }

        public TemporarySkillsSource Source { get; }

        public MiningSkillsSourceReader Reader { get; }

        public void AppendText(string text) => Source.AppendText(text);

        public void AppendBytes(byte[] bytes) => Source.AppendBytes(bytes);

        public void Dispose() => Source.Dispose();
    }

    private sealed class TemporarySkillsSource : IDisposable
    {
        private readonly string _directoryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");

        public TemporarySkillsSource(string fileName = "_Skills.2026-09-12.txt")
        {
            Directory.CreateDirectory(_directoryPath);
            Path = System.IO.Path.Combine(_directoryPath, fileName);
            File.WriteAllText(Path, string.Empty, Utf8WithoutBom);
        }

        public string Path { get; }

        public void WriteText(string text) => File.WriteAllText(Path, text, Utf8WithoutBom);

        public void AppendText(string text) => File.AppendAllText(Path, text, Utf8WithoutBom);

        public void AppendBytes(byte[] bytes)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes);
        }

        public MiningSkillsSourceReader CreateReaderAndCaptureBoundary()
        {
            var reader = new MiningSkillsSourceReader(Path);
            reader.CaptureSessionBoundary();
            return reader;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
