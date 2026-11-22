using System.IO;
using System.Security.Cryptography;
using System.Text;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public enum EventCheckpointVerificationResult
{
    Valid,
    Truncated,
    Replaced,
    RequiresRetry
}

public sealed class EventLogOversizedLineException(int maximumLineBytes)
    : Exception($"Event log line exceeds the {maximumLineBytes}-byte reader limit.")
{
    public int MaximumLineBytes { get; } = maximumLineBytes;
}

internal sealed record EventFileContinuitySignature(
    DateTime CreationTimeUtc,
    string PrefixHash,
    string MiddleHash);

public sealed class EventLogReader
{
    private const int FingerprintWindowSize = 4096;
    private const int ReadBufferSize = 64 * 1024;
    private readonly RuntimeDiagnosticService _diagnostics;
    private readonly int _maximumReadBytes;
    private readonly int _maximumLineBytes;

    public EventLogReader(RuntimeDiagnosticService? diagnostics = null)
        : this(ReadBufferSize, ReadBufferSize, diagnostics)
    {
    }

    internal EventLogReader(
        int maximumReadBytes,
        int maximumLineBytes,
        RuntimeDiagnosticService? diagnostics = null)
    {
        if (maximumReadBytes < 4)
            throw new ArgumentOutOfRangeException(nameof(maximumReadBytes));
        if (maximumLineBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLineBytes));
        _maximumReadBytes = maximumReadBytes;
        _maximumLineBytes = maximumLineBytes;
        _diagnostics = diagnostics ?? new RuntimeDiagnosticService();
    }

    public EventLogReadResult Read(
        string filePath,
        long startOffset,
        long endOffsetExclusive,
        EventFileFingerprint? fingerprint,
        string pendingText)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        if (endOffsetExclusive < startOffset)
            throw new ArgumentOutOfRangeException(nameof(endOffsetExclusive));

        return ReadCore(filePath, startOffset, endOffsetExclusive, fingerprint, pendingText);
    }

    public EventCheckpointVerificationResult VerifyCheckpoint(
        string filePath,
        long position,
        EventFileFingerprint fingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentNullException.ThrowIfNull(fingerprint);

        try
        {
            using var stream = Open(filePath);
            if (position > stream.Length)
                return EventCheckpointVerificationResult.Truncated;

            return CreateFingerprint(stream, position) == fingerprint
                ? EventCheckpointVerificationResult.Valid
                : EventCheckpointVerificationResult.Replaced;
        }
        catch (IOException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(EventLogReader), "Event log checkpoint verification failed for '{0}'. Exception={1}", filePath, exception.GetType().Name);
            return EventCheckpointVerificationResult.RequiresRetry;
        }
        catch (UnauthorizedAccessException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(EventLogReader), "Event log checkpoint verification was denied for '{0}'. Exception={1}", filePath, exception.GetType().Name);
            return EventCheckpointVerificationResult.RequiresRetry;
        }
    }

    private EventLogReadResult ReadCore(
        string filePath,
        long position,
        long endOffsetExclusive,
        EventFileFingerprint? fingerprint,
        string pendingText)
    {
        try
        {
            using var stream = Open(filePath);

            var readPosition = position;
            var readPendingText = pendingText;
            if (readPosition > stream.Length)
            {
                return CreateContinuityLossResult(stream, StreamGapReason.Truncated);
            }

            if (endOffsetExclusive > stream.Length)
                return CreateRetryResult(position, fingerprint, pendingText);

            if (readPosition > 0 && fingerprint is not null)
            {
                var currentFingerprint = CreateFingerprint(stream, readPosition);
                if (currentFingerprint != fingerprint)
                {
                    return CreateContinuityLossResult(stream, StreamGapReason.Replaced);
                }
            }

            return ReadBounded(stream, readPosition, endOffsetExclusive, readPendingText);
        }
        catch (DecoderFallbackException)
        {
            return CreateRetryResult(position, fingerprint, pendingText);
        }
        catch (IOException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(EventLogReader), "Event log read failed for '{0}'. Exception={1}", filePath, exception.GetType().Name);
            return CreateRetryResult(position, fingerprint, pendingText);
        }
        catch (UnauthorizedAccessException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(EventLogReader), "Event log read was denied for '{0}'. Exception={1}", filePath, exception.GetType().Name);
            return CreateRetryResult(position, fingerprint, pendingText);
        }
    }

    private EventLogReadResult ReadBounded(
        Stream stream,
        long position,
        long requestedEnd,
        string pendingText)
    {
        EnsurePendingWithinLimit(pendingText);

        var byteCount = (int)Math.Min(requestedEnd - position, _maximumReadBytes);
        var bytes = new byte[byteCount];
        stream.Seek(position, SeekOrigin.Begin);
        stream.ReadExactly(bytes);

        // A per-poll cap can land inside a UTF-8 scalar. Leave those bytes for
        // the next poll; an incomplete scalar at the actual captured EOF still
        // follows the existing RequiresRetry contract.
        var decodedByteCount = position + byteCount < requestedEnd
            ? GetUtf8SafePrefixLength(bytes)
            : byteCount;
        var newPosition = position + decodedByteCount;
        var byteOffset = position == 0 && bytes.AsSpan(0, decodedByteCount).StartsWith(Encoding.UTF8.Preamble)
            ? Encoding.UTF8.Preamble.Length
            : 0;
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var text = strictUtf8.GetString(bytes, byteOffset, decodedByteCount - byteOffset);
        var (lines, newPendingText) = SplitBoundedLines(pendingText + text);
        var newFingerprint = CreateFingerprint(stream, newPosition);

        return new EventLogReadResult(
            lines, newPosition, newFingerprint,
            RequiresRetry: false, newPendingText);
    }

    private static int GetUtf8SafePrefixLength(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 1; index >= Math.Max(0, bytes.Length - 4); index--)
        {
            var value = bytes[index];
            if ((value & 0b1100_0000) == 0b1000_0000)
                continue;

            return GetUtf8SequenceLength(value) > bytes.Length - index
                ? index
                : bytes.Length;
        }

        return bytes.Length;
    }

    private (IReadOnlyList<string> Lines, string PendingText) SplitBoundedLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
                continue;

            var end = index > start && text[index - 1] == '\r' ? index - 1 : index;
            EnsureLineWithinLimit(text.AsSpan(start, end - start));
            lines.Add(text[start..end]);
            start = index + 1;
        }

        var pending = text[start..];
        EnsurePendingWithinLimit(pending);
        return (lines, pending);
    }

    private void EnsurePendingWithinLimit(string pending)
    {
        var bytes = Encoding.UTF8.GetByteCount(pending);
        if (bytes > _maximumLineBytes
            && !(pending.EndsWith('\r') && bytes == (long)_maximumLineBytes + 1))
            throw new EventLogOversizedLineException(_maximumLineBytes);
    }

    private void EnsureLineWithinLimit(ReadOnlySpan<char> line)
    {
        if (Encoding.UTF8.GetByteCount(line) > _maximumLineBytes)
            throw new EventLogOversizedLineException(_maximumLineBytes);
    }

    internal (long Position, EventFileFingerprint Fingerprint) CaptureSnapshot(string filePath)
    {
        using var stream = Open(filePath);
        return CaptureSafeEofBaseline(stream);
    }

    // EventPipeline-only evidence in addition to the shared suffix fingerprint. Sparse
    // fixed windows avoid hashing the complete historical log on each poll.
    internal EventFileContinuitySignature? CaptureContinuitySignature(
        string filePath,
        long position,
        EventFileFingerprint expectedFingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        using var stream = Open(filePath);
        if (stream.Length < position || CreateFingerprint(stream, position) != expectedFingerprint)
            return null;

        var windowLength = (int)Math.Min(position, FingerprintWindowSize);
        return new EventFileContinuitySignature(
            File.GetCreationTimeUtc(filePath),
            HashWindow(stream, 0, windowLength),
            HashWindow(stream, (position - windowLength) / 2, windowLength));
    }

    private static FileStream Open(string filePath) => new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite);

    private static (long Position, EventFileFingerprint Fingerprint) CaptureSafeEofBaseline(Stream stream)
    {
        var position = GetUtf8SafeSnapshotPosition(stream);
        return (position, CreateFingerprint(stream, position));
    }

    private static EventLogReadResult CreateRetryResult(
        long position,
        EventFileFingerprint? fingerprint,
        string pendingText) =>
        new(
            [],
            position,
            fingerprint,
            RequiresRetry: true,
            pendingText);

    private static EventLogReadResult CreateContinuityLossResult(
        Stream stream,
        StreamGapReason reason)
    {
        var (newPosition, fingerprint) = CaptureSafeEofBaseline(stream);

        return new EventLogReadResult(
            [],
            newPosition,
            fingerprint,
            RequiresRetry: false,
            string.Empty)
        {
            ContinuityLossReason = reason
        };
    }

    private static long GetUtf8SafeSnapshotPosition(Stream stream)
    {
        var endPosition = stream.Length;
        var bufferLength = (int)Math.Min(4, endPosition);
        if (bufferLength == 0)
            return 0;

        var originalPosition = stream.Position;
        try
        {
            stream.Seek(endPosition - bufferLength, SeekOrigin.Begin);
            var bytes = new byte[bufferLength];
            stream.ReadExactly(bytes);

            for (var index = bytes.Length - 1; index >= 0; index--)
            {
                var value = bytes[index];
                if ((value & 0b1100_0000) == 0b1000_0000)
                    continue;

                var sequenceLength = GetUtf8SequenceLength(value);
                var availableLength = bytes.Length - index;
                return sequenceLength > availableLength
                    ? endPosition - availableLength
                    : endPosition;
            }

            return endPosition;
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static int GetUtf8SequenceLength(byte value) => value switch
    {
        <= 0b0111_1111 => 1,
        >= 0b1100_0010 and <= 0b1101_1111 => 2,
        >= 0b1110_0000 and <= 0b1110_1111 => 3,
        >= 0b1111_0000 and <= 0b1111_0100 => 4,
        _ => 1
    };

    private static EventFileFingerprint CreateFingerprint(Stream stream, long position)
    {
        if (position == 0)
            return new EventFileFingerprint(0, null);

        var windowLength = (int)Math.Min(position, FingerprintWindowSize);
        var originalPosition = stream.Position;

        try
        {
            stream.Seek(position - windowLength, SeekOrigin.Begin);
            var bytes = new byte[windowLength];
            stream.ReadExactly(bytes);
            return new EventFileFingerprint(
                windowLength,
                Convert.ToBase64String(SHA256.HashData(bytes)));
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static string HashWindow(Stream stream, long offset, int length)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }

}
