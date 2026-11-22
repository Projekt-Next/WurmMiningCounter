using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class ProspectingSequenceAssembler
{
    private bool _isActive;
    private bool _isPlainRock;
    private bool _awaitingDirectRockSaltSalt;
    private VeinMaterial? _material;
    private QualityMeasurement? _quality;
    private ProspectingObservationUpdate? _pendingObservationUpdate;

    public ProspectingSequenceDiagnostic? LastRecognizedEvent { get; private set; }

    internal bool IsAwaitingFirstMaterial => _isActive && !_isPlainRock && _material is null;

    public ProspectingResult? Process(string text)
    {
        LastRecognizedEvent = null;

        if (ProspectingLineParser.IsProspectingStart(text))
        {
            Reset();
            _isActive = true;
            LastRecognizedEvent = new ProspectingSequenceDiagnostic(ProspectingSequenceEventKind.Start);
            return null;
        }

        if (ProspectingLineParser.IsProspectingStop(text))
        {
            Reset();
            LastRecognizedEvent = new ProspectingSequenceDiagnostic(ProspectingSequenceEventKind.Stop);
            return null;
        }

        if (!_isActive)
        {
            return null;
        }

        if (_isPlainRock)
        {
            if (ProspectingLineParser.TryParseExactMaxQuality(text, out var quality))
            {
                var plainRock = new ProspectingResult.PlainRock(
                    quality,
                    ImmutableArray<ProspectingAugment>.Empty);
                Reset();
                return plainRock;
            }

            if (ProspectingLineParser.TryParseQualityGrade(text, out var grade))
            {
                var plainRock = new ProspectingResult.PlainRock(
                    grade,
                    ImmutableArray<ProspectingAugment>.Empty);
                Reset();
                return plainRock;
            }

            return null;
        }

        if (_material is null)
        {
            if (ProspectingLineParser.IsPlainRock(text))
            {
                _isPlainRock = true;
                return new ProspectingResult.PlainRock(
                    new QualityMeasurement.Unknown(),
                    ImmutableArray<ProspectingAugment>.Empty);
            }

            if (ProspectingLineParser.TryParseMaterial(text, out var material))
            {
                _material = material;
                QueueObservationUpdate(isSynchronized: false);
                LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                    ProspectingSequenceEventKind.Material,
                    Material: material);
            }

            return null;
        }

        if (_quality is null)
        {
            if (ProspectingLineParser.TryParseExactMaxQuality(text, out var quality))
            {
                _quality = quality;
                _awaitingDirectRockSaltSalt = _material == VeinMaterial.RockSalt;
                QueueObservationUpdate(isSynchronized: false);
                LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                    ProspectingSequenceEventKind.Quality,
                    Quality: quality);
                return null;
            }

            if (ProspectingLineParser.TryParseQualityGrade(text, out var grade))
            {
                _quality = grade;
                _awaitingDirectRockSaltSalt = _material == VeinMaterial.RockSalt;
                QueueObservationUpdate(isSynchronized: false);
                LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                    ProspectingSequenceEventKind.Quality,
                    Quality: grade);
            }

            return null;
        }

        if (_awaitingDirectRockSaltSalt
            && ProspectingLineParser.TryParseAugment(text, out var augment)
            && augment == ProspectingAugment.Salt)
        {
            var result = new ProspectingResult.Vein(
                VeinMaterial.RockSalt,
                _quality,
                new RemainingMeasurement.Unknown(),
                ImmutableArray<ProspectingAugment>.Empty);
            QueueObservationUpdate(new RemainingMeasurement.Unknown(), isSynchronized: true);
            ResetSequence();
            LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                ProspectingSequenceEventKind.Remaining,
                Remaining: new RemainingMeasurement.Unknown());
            return result;
        }

        _awaitingDirectRockSaltSalt = false;

        if (ProspectingLineParser.TryParseExactRemaining(text, out var remaining))
        {
            var result = CreateVeinResult(remaining);
            QueueObservationUpdate(remaining, isSynchronized: true);
            ResetSequence();
            LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                ProspectingSequenceEventKind.Remaining,
                Remaining: remaining);
            return result;
        }

        if (ProspectingLineParser.TryParseEstimateBand(text, out var estimateBand))
        {
            var result = CreateVeinResult(estimateBand);
            QueueObservationUpdate(estimateBand, isSynchronized: true);
            ResetSequence();
            LastRecognizedEvent = new ProspectingSequenceDiagnostic(
                ProspectingSequenceEventKind.Remaining,
                Remaining: estimateBand);
            return result;
        }

        return null;
    }

    private ProspectingResult.Vein CreateVeinResult(RemainingMeasurement remaining)
    {
        return new ProspectingResult.Vein(
            _material!.Value,
            _quality!,
            remaining,
            ImmutableArray<ProspectingAugment>.Empty);
    }

    public bool TryTakeObservationUpdate(out ProspectingObservationUpdate update)
    {
        if (_pendingObservationUpdate is not { } pendingUpdate)
        {
            update = null!;
            return false;
        }

        _pendingObservationUpdate = null;
        update = pendingUpdate;
        return true;
    }

    public void Reset()
    {
        ResetSequence();
        _pendingObservationUpdate = null;
        LastRecognizedEvent = null;
    }

    private void QueueObservationUpdate(bool isSynchronized)
        => QueueObservationUpdate(new RemainingMeasurement.Unknown(), isSynchronized);

    private void QueueObservationUpdate(
        RemainingMeasurement remaining,
        bool isSynchronized)
    {
        _pendingObservationUpdate = new ProspectingObservationUpdate(
            _material!.Value,
            _quality ?? new QualityMeasurement.Unknown(),
            remaining,
            isSynchronized);
    }

    private void ResetSequence()
    {
        _isActive = false;
        _isPlainRock = false;
        _awaitingDirectRockSaltSalt = false;
        _material = null;
        _quality = null;
    }
}

public enum ProspectingSequenceEventKind
{
    Start,
    Stop,
    Failure,
    Material,
    Quality,
    Remaining
}

public sealed record ProspectingSequenceDiagnostic(
    ProspectingSequenceEventKind Kind,
    VeinMaterial? Material = null,
    QualityMeasurement? Quality = null,
    RemainingMeasurement? Remaining = null);
