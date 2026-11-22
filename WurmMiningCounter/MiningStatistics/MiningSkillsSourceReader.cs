using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Reads only newly appended complete lines from one explicitly selected _Skills source.
/// It has no watcher, polling loop, source selection or lifecycle integration.
/// </summary>
public sealed class MiningSkillsSourceReader
{
    private const int FingerprintWindowSize = 4096;
    private const int ReadBufferSize = 64 * 1024;
    internal const int MaximumRecordBytes = 64 * 1024;
    private static readonly Regex SourceFileNamePattern = new(
        @"^_Skills\.\d{4}-\d{2}(?:-\d{2})?\.txt\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private long _position;
    private SourceFingerprint? _fingerprint;
    private readonly List<byte> _pendingBytes = [];
    private MiningSkillsSourceContinuityLossReason? _continuityLossReason;
    private bool _baselineCaptured;
    private readonly Func<Stream, long, SourceFingerprint> _fingerprintFactory;

    public MiningSkillsSourceReader(string canonicalSourcePath)
        : this(canonicalSourcePath, CreateFingerprint)
    {
    }

    internal MiningSkillsSourceReader(
        string canonicalSourcePath,
        Func<Stream, long, SourceFingerprint> fingerprintFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);
        ArgumentNullException.ThrowIfNull(fingerprintFactory);

        CanonicalSourcePath = Path.GetFullPath(canonicalSourcePath);
        _fingerprintFactory = fingerprintFactory;
        if (!SourceFileNamePattern.IsMatch(Path.GetFileName(CanonicalSourcePath)))
        {
            throw new ArgumentException(
                "The source must be named _Skills.YYYY-MM.txt or _Skills.YYYY-MM-DD.txt.",
                nameof(canonicalSourcePath));
        }
    }

    public string CanonicalSourcePath { get; }

    internal int PendingByteCountForTest => _pendingBytes.Count;

    /// <summary>
    /// Captures the startup boundary immediately after the last complete LF-terminated record.
    /// The same reader subsequently reads live bytes from that boundary.
    /// </summary>
    public MiningSkillsSourceBoundary CaptureSessionBoundary()
    {
        ThrowIfBaselineCaptured();
        using var stream = OpenSource();
        var position = FindLastCompleteRecordBoundary(stream);
        CaptureBaseline(stream, position);
        return new MiningSkillsSourceBoundary(CanonicalSourcePath, position, _fingerprint!);
    }

    /// <summary>
    /// Establishes a baseline at the beginning of a source created after monitoring started.
    /// </summary>
    internal void CaptureStartOfFileBaseline()
    {
        ThrowIfBaselineCaptured();
        using var stream = OpenSource();
        CaptureBaseline(stream, 0);
    }

    public MiningSkillsSourceReadResult ReadNewLines()
    {
        if (!_baselineCaptured)
            throw new InvalidOperationException("A source baseline must be captured before reading the _Skills source.");

        if (_continuityLossReason is { } existingLoss)
            return CreateContinuityLossResult(existingLoss);

        using var stream = OpenSource();
        if (stream.Length < _position)
            return MarkContinuityLoss(MiningSkillsSourceContinuityLossReason.Truncated);

        if (_position > 0 && _fingerprint is not null && _fingerprintFactory(stream, _position) != _fingerprint)
            return MarkContinuityLoss(MiningSkillsSourceContinuityLossReason.Replaced);

        var byteCount = (int)Math.Min(stream.Length - _position, ReadBufferSize);
        if (byteCount == 0)
            return new MiningSkillsSourceReadResult([], null);

        var originalPending = _pendingBytes.ToArray();
        var lines = new List<string>();
        var bytes = new byte[byteCount];
        stream.Seek(_position, SeekOrigin.Begin);
        stream.ReadExactly(bytes);

        try
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n')
                {
                    AddCompleteLine(lines);
                    continue;
                }

                if (!TryAppendPendingByte(value))
                {
                    _pendingBytes.Clear();
                    return MarkContinuityLoss(MiningSkillsSourceContinuityLossReason.OversizedRecord);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            _pendingBytes.Clear();
            _pendingBytes.AddRange(originalPending);
            return new MiningSkillsSourceReadResult([], null);
        }

        var newPosition = checked(_position + byteCount);
        var newFingerprint = _fingerprintFactory(stream, newPosition);

        // Commit only after the complete read cycle, including fingerprinting, has succeeded.
        _position = newPosition;
        _fingerprint = newFingerprint;

        return new MiningSkillsSourceReadResult(lines, null);
    }

    private FileStream OpenSource() => new(
        CanonicalSourcePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite);

    private MiningSkillsSourceReadResult MarkContinuityLoss(MiningSkillsSourceContinuityLossReason reason)
    {
        _pendingBytes.Clear();
        _continuityLossReason = reason;
        return CreateContinuityLossResult(reason);
    }

    private static MiningSkillsSourceReadResult CreateContinuityLossResult(
        MiningSkillsSourceContinuityLossReason reason) =>
        new([], reason);

    internal static SourceFingerprint CreateFingerprint(Stream stream, long position)
    {
        if (position == 0)
            return new SourceFingerprint(0, null);

        var windowLength = (int)Math.Min(position, FingerprintWindowSize);
        var originalPosition = stream.Position;

        try
        {
            stream.Seek(position - windowLength, SeekOrigin.Begin);
            var bytes = new byte[windowLength];
            stream.ReadExactly(bytes);
            return new SourceFingerprint(windowLength, Convert.ToBase64String(SHA256.HashData(bytes)));
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private bool TryAppendPendingByte(byte value)
    {
        if (_pendingBytes.Count < MaximumRecordBytes
            || (_pendingBytes.Count == MaximumRecordBytes && value == (byte)'\r'))
        {
            _pendingBytes.Add(value);
            return true;
        }

        return false;
    }

    private void AddCompleteLine(ICollection<string> lines)
    {
        var byteCount = _pendingBytes.Count;
        if (byteCount > 0 && _pendingBytes[^1] == (byte)'\r')
            byteCount--;

        var line = StrictUtf8.GetString(CollectionsMarshal.AsSpan(_pendingBytes)[..byteCount]);
        _pendingBytes.Clear();
        lines.Add(line);
    }

    internal sealed record SourceFingerprint(int WindowLength, string? Hash);

    private void CaptureBaseline(Stream stream, long position)
    {
        ThrowIfBaselineCaptured();

        _position = position;
        _fingerprint = _fingerprintFactory(stream, _position);
        _pendingBytes.Clear();
        _continuityLossReason = null;
        _baselineCaptured = true;
    }

    private void ThrowIfBaselineCaptured()
    {
        if (_baselineCaptured)
            throw new InvalidOperationException("The _Skills source baseline has already been captured.");
    }

    private static long FindLastCompleteRecordBoundary(Stream stream)
    {
        const int bufferSize = 4096;
        var length = stream.Length;
        var buffer = new byte[bufferSize];
        var end = length;

        while (end > 0)
        {
            var count = (int)Math.Min(buffer.Length, end);
            var start = end - count;
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer.AsSpan(0, count));

            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                    return start + index + 1;
            }

            end = start;
        }

        return 0;
    }
}
