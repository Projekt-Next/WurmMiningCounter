using System.IO;
using System.Text;

namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Finds the latest complete numeric Mining update before one already-captured source boundary.
/// </summary>
public sealed class MiningCurrentBootstrapper
{
    private const int BlockSize = 4096;
    private const int MaximumRecordLength = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public MiningCurrentBootstrapResult Find(string canonicalSourcePath, MiningSkillsSourceBoundary boundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);
        ArgumentNullException.ThrowIfNull(boundary);

        var normalizedPath = Path.GetFullPath(canonicalSourcePath);
        if (!string.Equals(normalizedPath, boundary.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase)
            || boundary.Position < 0)
        {
            return MiningCurrentBootstrapResult.InvalidBoundary;
        }

        using var stream = new FileStream(
            normalizedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        if (stream.Length < boundary.Position)
        {
            return new MiningCurrentBootstrapResult(
                MiningCurrentBootstrapStatus.ContinuityLost,
                ContinuityLossReason: MiningSkillsSourceContinuityLossReason.Truncated);
        }

        if (boundary.Position > 0
            && MiningSkillsSourceReader.CreateFingerprint(stream, boundary.Position) != boundary.Fingerprint)
        {
            return new MiningCurrentBootstrapResult(
                MiningCurrentBootstrapStatus.ContinuityLost,
                ContinuityLossReason: MiningSkillsSourceContinuityLossReason.Replaced);
        }

        var current = FindLatestMiningCurrent(stream, boundary.Position);
        return current is { } value
            ? new MiningCurrentBootstrapResult(MiningCurrentBootstrapStatus.Found, value)
            : MiningCurrentBootstrapResult.NotFound;
    }

    private static decimal? FindLatestMiningCurrent(Stream stream, long boundary)
    {
        var buffer = new byte[BlockSize];
        var trailing = Array.Empty<byte>();
        var trailingTooLong = false;
        var end = boundary;

        while (end > 0)
        {
            var count = (int)Math.Min(buffer.Length, end);
            var start = end - count;
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer.AsSpan(0, count));

            var segmentEnd = count;
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n')
                    continue;

                var recordLength = segmentEnd - index - 1 + trailing.Length;
                if (!trailingTooLong
                    && recordLength <= MaximumRecordLength
                    && TryGetMiningCurrent(Combine(buffer.AsSpan(index + 1, segmentEnd - index - 1), trailing), out var current))
                {
                    return current;
                }

                trailing = Array.Empty<byte>();
                trailingTooLong = false;
                segmentEnd = index;
            }

            Prepend(buffer.AsSpan(0, segmentEnd), ref trailing, ref trailingTooLong);
            end = start;
        }

        return trailingTooLong || !TryGetMiningCurrent(trailing, allowUtf8Bom: true, out var firstCurrent)
            ? null
            : firstCurrent;
    }

    private static bool TryGetMiningCurrent(byte[] bytes, out decimal currentValue) =>
        TryGetMiningCurrent(bytes, allowUtf8Bom: false, out currentValue);

    private static bool TryGetMiningCurrent(byte[] bytes, bool allowUtf8Bom, out decimal currentValue)
    {
        currentValue = default;
        if (bytes.Length == 0)
            return false;

        try
        {
            var line = StrictUtf8.GetString(bytes);
            if (allowUtf8Bom && line.Length > 0 && line[0] == '\uFEFF')
                line = line[1..];
            if (line.EndsWith('\r'))
                line = line[..^1];

            var update = NumericSkillUpdateParser.Parse(line);
            if (update is null || !string.Equals(update.SkillName, "Mining", StringComparison.Ordinal))
                return false;

            currentValue = update.CurrentValue;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] Combine(ReadOnlySpan<byte> prefix, byte[] suffix)
    {
        var result = new byte[prefix.Length + suffix.Length];
        prefix.CopyTo(result);
        suffix.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private static void Prepend(
        ReadOnlySpan<byte> prefix,
        ref byte[] trailing,
        ref bool trailingTooLong)
    {
        if (trailingTooLong || prefix.Length + trailing.Length > MaximumRecordLength)
        {
            trailing = Array.Empty<byte>();
            trailingTooLong = true;
            return;
        }

        trailing = Combine(prefix, trailing);
    }
}
