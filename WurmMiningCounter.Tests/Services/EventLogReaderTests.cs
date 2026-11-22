using System.Text;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLogReaderTests
{
    private readonly EventLogReader _reader = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Read_WhenNewCompleteLineStartsAtSpecifiedPosition_ReturnsOnlyNewLine()
    {
        using var file = new TemporaryLogFile();
        const string existingText = "Earlier line\n";
        File.WriteAllText(file.Path, existingText + "New line\n", Utf8WithoutBom);

        var result = ReadToCurrentEnd(file.Path, Utf8WithoutBom.GetByteCount(existingText), null, string.Empty);

        Assert.Equal(["New line"], result.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, result.NewPosition);
        Assert.Empty(result.PendingText);
        Assert.False(result.RequiresRetry);
    }

    [Fact]
    public void Read_WhenSeveralCompleteLinesAreAvailable_ReturnsAllLines()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "First\nSecond\nThird\n", Utf8WithoutBom);

        var result = ReadToCurrentEnd(file.Path, 0, null, string.Empty);

        Assert.Equal(["First", "Second", "Third"], result.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, result.NewPosition);
        Assert.Empty(result.PendingText);
        Assert.False(result.RequiresRetry);
        Assert.Null(result.ContinuityLossReason);
    }

    [Fact]
    public void Read_WhenLineIsCompletedInNextRead_CombinesItWithPendingText()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Mining ore", Utf8WithoutBom);

        var firstResult = ReadToCurrentEnd(file.Path, 0, null, string.Empty);
        File.AppendAllText(file.Path, " successfully.\n", Utf8WithoutBom);

        var secondResult = ReadToCurrentEnd(
            file.Path,
            firstResult.NewPosition,
            firstResult.Fingerprint,
            firstResult.PendingText);

        Assert.Empty(firstResult.Lines);
        Assert.Equal("Mining ore", firstResult.PendingText);
        Assert.Equal(["Mining ore successfully."], secondResult.Lines);
        Assert.Empty(secondResult.PendingText);
        Assert.False(secondResult.RequiresRetry);
    }

    [Fact]
    public void Read_WhenLinesUseCrLf_RemovesCarriageReturns()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "First\r\nSecond\r\n", Utf8WithoutBom);

        var result = ReadToCurrentEnd(file.Path, 0, null, string.Empty);

        Assert.Equal(["First", "Second"], result.Lines);
    }

    [Fact]
    public void Read_WhenUtf8BomIsPresentAtPositionZero_OmitsBomFromLine()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllBytes(file.Path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Event line\n")).ToArray());

        var result = ReadToCurrentEnd(file.Path, 0, null, string.Empty);

        Assert.Equal(["Event line"], result.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, result.NewPosition);
    }

    [Fact]
    public void Read_WhenNoNewDataIsAvailable_ReturnsEmptyLinesAndKeepsPosition()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Event line\n", Utf8WithoutBom);

        var firstResult = ReadToCurrentEnd(file.Path, 0, null, string.Empty);
        var secondResult = ReadToCurrentEnd(
            file.Path,
            firstResult.NewPosition,
            firstResult.Fingerprint,
            firstResult.PendingText);

        Assert.Empty(secondResult.Lines);
        Assert.Equal(firstResult.NewPosition, secondResult.NewPosition);
        Assert.Equal(firstResult.Fingerprint, secondResult.Fingerprint);
        Assert.Empty(secondResult.PendingText);
        Assert.False(secondResult.RequiresRetry);
    }

    [Fact]
    public void Read_WhenFileWasTruncated_ReturnsTruncatedContinuityLossAtEndOfFile()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Previous incomplete text", Utf8WithoutBom);

        var firstResult = ReadToCurrentEnd(file.Path, 0, null, string.Empty);
        File.WriteAllText(file.Path, "New\n", Utf8WithoutBom);
        var expectedBaseline = ReadToCurrentEnd(file.Path, 0, null, string.Empty);

        var secondResult = ReadToCurrentEnd(
            file.Path,
            firstResult.NewPosition,
            firstResult.Fingerprint,
            firstResult.PendingText);

        Assert.Equal(StreamGapReason.Truncated, secondResult.ContinuityLossReason);
        Assert.Empty(secondResult.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, secondResult.NewPosition);
        Assert.Empty(secondResult.PendingText);
        Assert.False(secondResult.RequiresRetry);
        Assert.Equal(expectedBaseline.Fingerprint, secondResult.Fingerprint);
    }

    [Fact]
    public void Read_WhenFingerprintDoesNotMatch_ReturnsReplacementContinuityLossAtEndOfFile()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Old fragment\n", Utf8WithoutBom);

        var firstResult = ReadToCurrentEnd(file.Path, 0, null, string.Empty);
        File.WriteAllText(file.Path, "Replacement content\n", Utf8WithoutBom);
        var expectedBaseline = ReadToCurrentEnd(file.Path, 0, null, string.Empty);

        var secondResult = ReadToCurrentEnd(
            file.Path,
            firstResult.NewPosition,
            firstResult.Fingerprint,
            firstResult.PendingText);

        Assert.True(firstResult.NewPosition <= new FileInfo(file.Path).Length);
        Assert.Equal(StreamGapReason.Replaced, secondResult.ContinuityLossReason);
        Assert.Empty(secondResult.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, secondResult.NewPosition);
        Assert.Empty(secondResult.PendingText);
        Assert.False(secondResult.RequiresRetry);
        Assert.Equal(expectedBaseline.Fingerprint, secondResult.Fingerprint);
    }

    [Fact]
    public void Read_WhenUtf8SequenceIsIncomplete_ReturnsRetryWithoutAdvancingPosition()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Complete\n", Utf8WithoutBom);
        var firstResult = ReadToCurrentEnd(file.Path, 0, null, string.Empty);
        using (var stream = new FileStream(file.Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.WriteByte(0xC3);

        var secondResult = ReadToCurrentEnd(
            file.Path,
            firstResult.NewPosition,
            firstResult.Fingerprint,
            firstResult.PendingText);

        Assert.True(secondResult.RequiresRetry);
        Assert.Empty(secondResult.Lines);
        Assert.Equal(firstResult.NewPosition, secondResult.NewPosition);
        Assert.Equal(firstResult.Fingerprint, secondResult.Fingerprint);
        Assert.Equal(firstResult.PendingText, secondResult.PendingText);
        Assert.Null(secondResult.ContinuityLossReason);
    }

    [Fact]
    public void Read_WhenLogPathCannotBeOpened_ReturnsRetryWithoutThrowing()
    {
        using var file = new TemporaryLogFile();
        var inaccessiblePath = file.Path;
        File.Delete(inaccessiblePath);
        Directory.CreateDirectory(inaccessiblePath);

        var exception = Record.Exception(() =>
            _reader.Read(inaccessiblePath, 0, 0, null, string.Empty));
        var result = _reader.Read(inaccessiblePath, 0, 0, null, string.Empty);

        Assert.Null(exception);
        Assert.True(result.RequiresRetry);
        Assert.Empty(result.Lines);
        Assert.Equal(0, result.NewPosition);
    }

    [Fact]
    public void BoundedRead_DoesNotIncludeBytesAppendedAfterBoundaryWasCaptured()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\n", Utf8WithoutBom);
        var firstBoundary = new FileInfo(file.Path).Length;
        File.AppendAllText(file.Path, "B\n", Utf8WithoutBom);

        var first = _reader.Read(file.Path, 0, firstBoundary, null, string.Empty);
        var second = _reader.Read(
            file.Path, first.NewPosition, new FileInfo(file.Path).Length,
            first.Fingerprint, first.PendingText);

        Assert.Equal(["A"], first.Lines);
        Assert.Equal(firstBoundary, first.NewPosition);
        Assert.Equal(["B"], second.Lines);
        Assert.Equal(new FileInfo(file.Path).Length, second.NewPosition);
    }

    [Fact]
    public void BoundedRead_LineExactlyAtByteLimitCanCompleteOnNextPoll()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        File.WriteAllText(file.Path, "12345678\n", Utf8WithoutBom);

        var first = reader.Read(file.Path, 0, 9, null, string.Empty);
        var second = reader.Read(file.Path, first.NewPosition, 9, first.Fingerprint, first.PendingText);

        Assert.Equal(8, first.NewPosition);
        Assert.Empty(first.Lines);
        Assert.Equal("12345678", first.PendingText);
        Assert.Equal(["12345678"], second.Lines);
        Assert.Equal(9, second.NewPosition);
        Assert.Empty(second.PendingText);
    }

    [Fact]
    public void BoundedRead_LineOneByteOverLimitFaultsWithoutReturningCheckpoint()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 16, maximumLineBytes: 8);
        File.WriteAllText(file.Path, "123456789\n", Utf8WithoutBom);

        var fault = Assert.Throws<EventLogOversizedLineException>(() =>
            reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty));

        Assert.Equal(8, fault.MaximumLineBytes);
    }

    [Fact]
    public void BoundedRead_LargeAppendConsumesAtMostOneChunkBeforeOversizedFault()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        File.WriteAllText(file.Path, new string('X', 32), Utf8WithoutBom);
        var capturedEnd = new FileInfo(file.Path).Length;

        var first = reader.Read(file.Path, 0, capturedEnd, null, string.Empty);
        Assert.Equal(8, first.NewPosition);
        Assert.Equal(new string('X', 8), first.PendingText);
        Assert.Throws<EventLogOversizedLineException>(() =>
            reader.Read(file.Path, first.NewPosition, capturedEnd, first.Fingerprint, first.PendingText));
    }

    [Fact]
    public void BoundedRead_PendingGrowthAcrossPollsFaultsAtLimitPlusOne()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        File.WriteAllText(file.Path, "1234", Utf8WithoutBom);
        var first = reader.Read(file.Path, 0, 4, null, string.Empty);
        File.AppendAllText(file.Path, "5678", Utf8WithoutBom);
        var second = reader.Read(file.Path, first.NewPosition, 8, first.Fingerprint, first.PendingText);
        File.AppendAllText(file.Path, "9", Utf8WithoutBom);

        Assert.Equal("12345678", second.PendingText);
        Assert.Equal(8, second.NewPosition);
        Assert.Throws<EventLogOversizedLineException>(() =>
            reader.Read(file.Path, second.NewPosition, 9, second.Fingerprint, second.PendingText));
        Assert.Equal(8, second.NewPosition);
    }

    [Fact]
    public void BoundedRead_ChunkBoundaryInsideUtf8ScalarLeavesBytesForNextPoll()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 4, maximumLineBytes: 5);
        File.WriteAllText(file.Path, "abcé\n", Utf8WithoutBom);
        var capturedEnd = new FileInfo(file.Path).Length;

        var first = reader.Read(file.Path, 0, capturedEnd, null, string.Empty);
        var second = reader.Read(
            file.Path, first.NewPosition, capturedEnd, first.Fingerprint, first.PendingText);

        Assert.Equal(3, first.NewPosition);
        Assert.Equal("abc", first.PendingText);
        Assert.Equal(["abcé"], second.Lines);
        Assert.Equal(capturedEnd, second.NewPosition);
    }

    [Fact]
    public void BoundedRead_MultipleNormalChunksPreserveOrderAndCapturedAppendBoundary()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 4, maximumLineBytes: 8);
        File.WriteAllText(file.Path, "A\nB\nC\n", Utf8WithoutBom);
        var capturedEnd = new FileInfo(file.Path).Length;
        File.AppendAllText(file.Path, "D\n", Utf8WithoutBom);

        var first = reader.Read(file.Path, 0, capturedEnd, null, string.Empty);
        var second = reader.Read(file.Path, first.NewPosition, capturedEnd, first.Fingerprint, first.PendingText);
        var third = reader.Read(
            file.Path, second.NewPosition, new FileInfo(file.Path).Length,
            second.Fingerprint, second.PendingText);

        Assert.Equal(["A", "B"], first.Lines);
        Assert.Equal(["C"], second.Lines);
        Assert.Equal(capturedEnd, second.NewPosition);
        Assert.Equal(["D"], third.Lines);
    }

    [Fact]
    public void BoundedRead_OversizedLineDoesNotSkipFollowingGoodLine()
    {
        using var file = new TemporaryLogFile();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        File.WriteAllText(file.Path, "123456789\nOK\n", Utf8WithoutBom);
        var capturedEnd = new FileInfo(file.Path).Length;

        var first = reader.Read(file.Path, 0, capturedEnd, null, string.Empty);
        Assert.Equal(8, first.NewPosition);
        Assert.Empty(first.Lines);
        Assert.Throws<EventLogOversizedLineException>(() =>
            reader.Read(file.Path, first.NewPosition, capturedEnd, first.Fingerprint, first.PendingText));
        Assert.Throws<EventLogOversizedLineException>(() =>
            reader.Read(file.Path, first.NewPosition, capturedEnd, first.Fingerprint, first.PendingText));
    }

    [Fact]
    public void BoundedRead_ReturnsSeveralCompleteLinesInPhysicalOrder()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\nB\nC\n", Utf8WithoutBom);

        var result = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);

        Assert.Equal(["A", "B", "C"], result.Lines);
    }

    [Fact]
    public void BoundedRead_PreservesPartialLineAcrossRangesWithoutDuplicates()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\npar", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        File.AppendAllText(file.Path, "tial\nB\n", Utf8WithoutBom);

        var second = _reader.Read(
            file.Path, first.NewPosition, new FileInfo(file.Path).Length,
            first.Fingerprint, first.PendingText);
        var unchanged = _reader.Read(
            file.Path, second.NewPosition, second.NewPosition,
            second.Fingerprint, second.PendingText);

        Assert.Equal(["A"], first.Lines);
        Assert.Equal("par", first.PendingText);
        Assert.Equal(["partial", "B"], second.Lines);
        Assert.Empty(second.PendingText);
        Assert.Empty(unchanged.Lines);
    }

    [Fact]
    public void BoundedRead_SplitUtf8RetriesWithoutChangingCheckpointOrPending()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllBytes(file.Path, Encoding.UTF8.GetBytes("A\npre").Concat(new byte[] { 0xC3 }).ToArray());
        var splitBoundary = new FileInfo(file.Path).Length;
        AppendBytes(file.Path, [0xA9, (byte)'\n']);

        var incomplete = _reader.Read(file.Path, 0, splitBoundary, null, "before");
        var complete = _reader.Read(
            file.Path, incomplete.NewPosition, new FileInfo(file.Path).Length,
            incomplete.Fingerprint, incomplete.PendingText);

        Assert.True(incomplete.RequiresRetry);
        Assert.Empty(incomplete.Lines);
        Assert.Equal(0, incomplete.NewPosition);
        Assert.Null(incomplete.Fingerprint);
        Assert.Equal("before", incomplete.PendingText);
        Assert.Equal(["beforeA", "preé"], complete.Lines);
        Assert.Empty(complete.PendingText);
    }

    [Fact]
    public void BoundedRead_WaitsForLfWhenCrLfCrossesRangeBoundary()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "First\r", Utf8WithoutBom);
        var firstBoundary = new FileInfo(file.Path).Length;
        File.AppendAllText(file.Path, "\nSecond\n", Utf8WithoutBom);

        var first = _reader.Read(file.Path, 0, firstBoundary, null, string.Empty);
        var second = _reader.Read(
            file.Path, first.NewPosition, new FileInfo(file.Path).Length,
            first.Fingerprint, first.PendingText);

        Assert.Empty(first.Lines);
        Assert.Equal("First\r", first.PendingText);
        Assert.Equal(["First", "Second"], second.Lines);
    }

    [Fact]
    public void BoundedRead_RejectsInvalidRangeWithoutAdvancingCheckpoint()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\n", Utf8WithoutBom);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _reader.Read(file.Path, -1, 0, null, "pending"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _reader.Read(file.Path, 2, 1, null, "pending"));

        var beyondLength = _reader.Read(file.Path, 0, 3, null, "pending");
        Assert.True(beyondLength.RequiresRetry);
        Assert.Empty(beyondLength.Lines);
        Assert.Equal(0, beyondLength.NewPosition);
        Assert.Equal("pending", beyondLength.PendingText);

        var valid = _reader.Read(file.Path, 0, 2, null, string.Empty);
        Assert.Equal(["A"], valid.Lines);
    }

    [Fact]
    public void VerifyCheckpoint_AtUnchangedLengthDoesNotAffectPendingOrNextRead()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\npar", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);

        var verification = _reader.VerifyCheckpoint(file.Path, first.NewPosition, first.Fingerprint!);
        File.AppendAllText(file.Path, "tial\n", Utf8WithoutBom);
        var second = _reader.Read(
            file.Path, first.NewPosition, new FileInfo(file.Path).Length,
            first.Fingerprint, first.PendingText);

        Assert.Equal(EventCheckpointVerificationResult.Valid, verification);
        Assert.Equal(["A"], first.Lines);
        Assert.Equal("par", first.PendingText);
        Assert.Equal(["partial"], second.Lines);
    }

    [Fact]
    public void VerifyCheckpoint_DetectsSameLengthReplacementWithoutIncrementalRead()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Old\n", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        File.WriteAllText(file.Path, "New\n", Utf8WithoutBom);

        var verification = _reader.VerifyCheckpoint(file.Path, first.NewPosition, first.Fingerprint!);

        Assert.Equal(EventCheckpointVerificationResult.Replaced, verification);
        Assert.Equal(4, first.NewPosition);
    }

    [Fact]
    public void ContinuitySignature_DetectsChangedPrefixDespiteIdenticalLengthAndSuffix()
    {
        using var file = new TemporaryLogFile();
        var suffix = new string('s', 4096);
        File.WriteAllText(file.Path, new string('a', 8192) + suffix, Utf8WithoutBom);
        var snapshot = _reader.CaptureSnapshot(file.Path);
        var original = _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint);

        File.WriteAllText(file.Path, new string('b', 8192) + suffix, Utf8WithoutBom);

        Assert.Equal(EventCheckpointVerificationResult.Valid,
            _reader.VerifyCheckpoint(file.Path, snapshot.Position, snapshot.Fingerprint));
        Assert.NotEqual(original, _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint));
    }

    [Fact]
    public void ContinuitySignature_NormalAppendPreservesPreviousCheckpointEvidence()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, new string('a', 8192) + "\n", Utf8WithoutBom);
        var snapshot = _reader.CaptureSnapshot(file.Path);
        var original = _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint);

        File.AppendAllText(file.Path, "later\n", Utf8WithoutBom);

        Assert.Equal(original, _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint));
    }

    [Fact]
    public void ContinuitySignature_RecreatedFileChangesIdentityBeyondSampledWindows()
    {
        using var file = new TemporaryLogFile();
        var originalBytes = new string('a', 32768).ToCharArray();
        File.WriteAllText(file.Path, new string(originalBytes), Utf8WithoutBom);
        var snapshot = _reader.CaptureSnapshot(file.Path);
        var original = _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint);
        var creationTime = File.GetCreationTimeUtc(file.Path);

        originalBytes[8192] = 'b'; // outside prefix, middle and suffix windows
        File.Delete(file.Path);
        File.WriteAllText(file.Path, new string(originalBytes), Utf8WithoutBom);
        File.SetCreationTimeUtc(file.Path, creationTime.AddDays(1));

        Assert.Equal(EventCheckpointVerificationResult.Valid,
            _reader.VerifyCheckpoint(file.Path, snapshot.Position, snapshot.Fingerprint));
        Assert.NotEqual(original, _reader.CaptureContinuitySignature(
            file.Path, snapshot.Position, snapshot.Fingerprint));
    }

    [Fact]
    public void CaptureSnapshot_SkipsHistoryButKeepsIncompleteUtf8AtEof()
    {
        using var file = new TemporaryLogFile();
        var history = Encoding.UTF8.GetBytes("History\n");
        File.WriteAllBytes(file.Path, history.Concat(new byte[] { 0xC3 }).ToArray());
        var snapshot = _reader.CaptureSnapshot(file.Path);
        AppendBytes(file.Path, [0xA9, (byte)'\n']);

        var result = _reader.Read(
            file.Path, snapshot.Position, new FileInfo(file.Path).Length,
            snapshot.Fingerprint, string.Empty);

        Assert.Equal(history.Length, snapshot.Position);
        Assert.Equal(["é"], result.Lines);
    }

    [Fact]
    public void BoundedRead_TruncateUsesSafeEofBaselineAndDoesNotReplayNewHistory()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Long old history\n", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        var newHistory = Encoding.UTF8.GetBytes("New\n");
        File.WriteAllBytes(file.Path, newHistory.Concat(new byte[] { 0xC3 }).ToArray());

        var lost = _reader.Read(
            file.Path, first.NewPosition, first.NewPosition,
            first.Fingerprint, first.PendingText);
        AppendBytes(file.Path, [0xA9, (byte)'\n']);
        var next = _reader.Read(
            file.Path, lost.NewPosition, new FileInfo(file.Path).Length,
            lost.Fingerprint, lost.PendingText);

        Assert.Equal(StreamGapReason.Truncated, lost.ContinuityLossReason);
        Assert.Empty(lost.Lines);
        Assert.Equal(newHistory.Length, lost.NewPosition);
        Assert.Empty(lost.PendingText);
        Assert.Equal(["é"], next.Lines);
    }

    [Fact]
    public void BoundedRead_ReplacementUsesSafeEofBaselineWithoutReplay()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "Old\n", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        File.WriteAllBytes(file.Path, Encoding.UTF8.GetBytes("New\n").Concat(new byte[] { 0xC3 }).ToArray());

        var lost = _reader.Read(
            file.Path, first.NewPosition, new FileInfo(file.Path).Length,
            first.Fingerprint, first.PendingText);
        AppendBytes(file.Path, [0xA9, (byte)'\n']);
        var next = _reader.Read(
            file.Path, lost.NewPosition, new FileInfo(file.Path).Length,
            lost.Fingerprint, lost.PendingText);

        Assert.Equal(StreamGapReason.Replaced, lost.ContinuityLossReason);
        Assert.Empty(lost.Lines);
        Assert.Equal(4, lost.NewPosition);
        Assert.Equal(["é"], next.Lines);
    }

    [Fact]
    public void BoundedRead_MissingFilePreservesCheckpointAndPending()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\npar", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        File.Delete(file.Path);

        var missing = _reader.Read(
            file.Path, first.NewPosition, first.NewPosition + 1,
            first.Fingerprint, first.PendingText);
        var verification = _reader.VerifyCheckpoint(file.Path, first.NewPosition, first.Fingerprint!);

        Assert.True(missing.RequiresRetry);
        Assert.Equal(first.NewPosition, missing.NewPosition);
        Assert.Equal(first.Fingerprint, missing.Fingerprint);
        Assert.Equal(first.PendingText, missing.PendingText);
        Assert.Equal(EventCheckpointVerificationResult.RequiresRetry, verification);
    }

    [Fact]
    public void BoundedRead_TransientIoCanRetrySameRange()
    {
        using var file = new TemporaryLogFile();
        File.WriteAllText(file.Path, "A\npar", Utf8WithoutBom);
        var first = _reader.Read(file.Path, 0, new FileInfo(file.Path).Length, null, string.Empty);
        File.AppendAllText(file.Path, "tial\n", Utf8WithoutBom);
        var boundary = new FileInfo(file.Path).Length;

        EventLogReadResult failed;
        using (new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = _reader.Read(
                file.Path, first.NewPosition, boundary,
                first.Fingerprint, first.PendingText);
        }

        var recovered = _reader.Read(
            file.Path, failed.NewPosition, boundary,
            failed.Fingerprint, failed.PendingText);

        Assert.True(failed.RequiresRetry);
        Assert.Equal(first.NewPosition, failed.NewPosition);
        Assert.Equal(first.Fingerprint, failed.Fingerprint);
        Assert.Equal(first.PendingText, failed.PendingText);
        Assert.Equal(["partial"], recovered.Lines);
    }

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    private EventLogReadResult ReadToCurrentEnd(
        string filePath,
        long position,
        EventFileFingerprint? fingerprint,
        string pendingText) =>
        _reader.Read(
            filePath,
            position,
            Math.Max(position, new FileInfo(filePath).Length),
            fingerprint,
            pendingText);

    private sealed class TemporaryLogFile : IDisposable
    {
        private readonly string _directoryPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");

        public TemporaryLogFile()
        {
            Directory.CreateDirectory(_directoryPath);
            Path = System.IO.Path.Combine(_directoryPath, "Event.txt");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
