using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class AnalyseSequenceAssembler
{
    private AnalyseInput? _input;
    private readonly List<AnalyseEntry> _entries = [];
    private bool _hasObservation;

    public AnalyseResult? Process(string text)
    {
        if (AnalyseLineParser.TryParseStart(text, out var input))
        {
            Reset();
            _input = input;
            return null;
        }

        if (AnalyseLineParser.TryParseFinish(text, out var finished))
        {
            if (_input != finished)
                return null;
            // A timestamped finish can match only after the normalizer has validated its prefix.
            var result = _hasObservation ? new AnalyseResult(finished, _entries.ToImmutableArray())
            {
                FinishTime = text[0] == '[' ? text.Substring(1, 8) : null
            } : null;
            Reset();
            return result;
        }

        if (_input is null)
            return null;
        if (AnalyseLineParser.TryParseTrace(text, out var trace))
        {
            _entries.Add(new AnalyseEntry(trace, null, null, text));
            _hasObservation = true;
        }
        else if (AnalyseLineParser.TryParseNoTrace(text, out var noTrace))
        {
            _entries.Add(new AnalyseEntry(null, noTrace, null, text));
            _hasObservation = true;
        }
        else if (AnalyseLineParser.TryParseStageMarker(text, out var marker))
        {
            _entries.Add(new AnalyseEntry(null, null, marker, text));
        }
        return null;
    }

    public void Reset()
    {
        _input = null;
        _entries.Clear();
        _hasObservation = false;
    }
}
