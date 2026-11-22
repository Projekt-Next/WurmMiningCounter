using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class VeinContextProcessor : IOrderedEventConsumer
{
    private const string ChipAwayAtRock = "You chip away at the rock.";
    private const string EnterCave = "You enter the cave.";
    private const string LeaveCave = "You leave the cave.";
    private const string DarkDungeonsProspectingFailure = "You see dark dungeons.";
    private const string SurfaceTooFlat =
        "You realize you can't open a tunnel here, because the surface is too flat.";

    private readonly ConcurrentDictionary<PlayerProfileId, ProfileVeinState> _profileStates = [];
    private readonly VeinExamineParser _examineParser = new();
    private readonly MiningActionContextParser _miningActionContextParser = new();
    private readonly MiningEventParser _miningEventParser = new();
    private readonly SessionBoundaryParser _sessionBoundaryParser = new();
    private readonly RuntimeDiagnosticService _diagnostics;
    private readonly Action<PlayerProfileId>? _invalidateNearbyScan;

    public VeinContextProcessor(
        RuntimeDiagnosticService? diagnostics = null,
        Action<PlayerProfileId>? invalidateNearbyScan = null)
    {
        _diagnostics = diagnostics ?? new RuntimeDiagnosticService();
        _invalidateNearbyScan = invalidateNearbyScan;
    }

    public event EventHandler<VeinContextChangedEventArgs>? ActiveContextChanged;

    public event EventHandler<PlainRockProspectingResultChangedEventArgs>? PlainRockProspectingResultChanged;

    public void Process(OrderedEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        switch (item)
        {
            case OrderedEventLine orderedEventLine:
                Debug.WriteLine(
                    $"[VEIN] profile={orderedEventLine.ProfileName} seq={orderedEventLine.SequenceNumber} text={orderedEventLine.Line.Text}");
                ProcessLine(orderedEventLine);
                break;
            case StreamContinuityLost:
                ClearProfileState(item.ProfileId, "StreamContinuityLost");
                break;
            case ResetProfileBarrier:
                ClearProfileState(item.ProfileId, "ResetProfile");
                break;
        }
    }

    public void Invalidate(PlayerProfileId profileId, string profileName)
    {
        ClearProfileState(profileId, "ConsumerInvalidated");
    }

    /// <summary>Clears retained Context state for a released profile by identity.</summary>
    internal void ResetProfile(PlayerProfileId profileId) => ClearProfileState(profileId, "ResetProfile");

    public bool TryGetActiveContext(PlayerProfileId profileId, out ActiveVeinContext? context)
    {
        context = null;

        if (!_profileStates.TryGetValue(profileId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            context = state.ActiveContext;
            return context is not null;
        }
    }

    public ImmutableArray<VeinContextSnapshot> GetActiveContextSnapshots()
    {
        var snapshots = ImmutableArray.CreateBuilder<VeinContextSnapshot>();

        foreach (var (profileId, state) in _profileStates)
        {
            lock (state.Sync)
            {
                if (state.ActiveContext is not { } context)
                    continue;

                snapshots.Add(CreateSnapshot(profileId, state.ProfileName, context));
            }
        }

        return snapshots.ToImmutable();
    }

    internal ImmutableArray<PlayerProfileId> GetPlainRockProspectingResultProfiles()
    {
        var profiles = ImmutableArray.CreateBuilder<PlayerProfileId>();

        foreach (var (profileId, state) in _profileStates)
        {
            lock (state.Sync)
            {
                if (state.LastPlainRockResult is not null)
                    profiles.Add(profileId);
            }
        }

        return profiles.ToImmutable();
    }

    public bool TryGetActiveMiningAttempt(PlayerProfileId profileId, out MiningAttempt? attempt)
    {
        attempt = null;

        if (!_profileStates.TryGetValue(profileId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            attempt = state.ActiveMiningAttempt;
            return attempt is not null;
        }
    }

    private void ProcessLine(OrderedEventLine orderedEventLine)
    {
        var transition = MiningAttemptTransitionParser.Parse(orderedEventLine.Line.Text);
        var state = _profileStates.GetOrAdd(
            orderedEventLine.ProfileId,
            _ => new ProfileVeinState(orderedEventLine.ProfileName));

        VeinContextChangedEventArgs? contextChanged;
        PlainRockProspectingResultChangedEventArgs? plainRockResultChanged;
        ProspectingSequenceDiagnostic? prospectingDiagnostic;
        string? clearReason;
        bool clearNearbyScan;
        VeinMaterial? destroyedMaterial;
        lock (state.Sync)
        {
            var previousContext = state.ActiveContext;
            var previousPlainRockResult = state.LastPlainRockResult;
            var isLocalExactDecrement = ProcessLineLocked(
                state,
                orderedEventLine,
                transition,
                out prospectingDiagnostic,
                out clearReason,
                out clearNearbyScan,
                out destroyedMaterial);
            contextChanged = CreateContextChangedEventArgs(
                orderedEventLine.ProfileId,
                orderedEventLine.ProfileName,
                previousContext,
                state.ActiveContext,
                isLocalExactDecrement);
            plainRockResultChanged = CreatePlainRockResultChangedEventArgs(
                orderedEventLine.ProfileId,
                orderedEventLine.ProfileName,
                previousPlainRockResult,
                state.LastPlainRockResult);
        }

        if (clearNearbyScan)
            _invalidateNearbyScan?.Invoke(orderedEventLine.ProfileId);

        ReportProspectingEvent(orderedEventLine.ProfileId, orderedEventLine.ProfileName, prospectingDiagnostic);
        ReportContextChange(orderedEventLine.ProfileName, contextChanged, clearReason);
        OnActiveContextChanged(contextChanged);
        OnPlainRockProspectingResultChanged(plainRockResultChanged);
    }

    private bool ProcessLineLocked(
        ProfileVeinState state,
        OrderedEventLine orderedEventLine,
        MiningAttemptTransitionKind? transition,
        out ProspectingSequenceDiagnostic? prospectingDiagnostic,
        out string? clearReason,
        out bool clearNearbyScan,
        out VeinMaterial? destroyedMaterial)
    {
        prospectingDiagnostic = null;
        clearReason = null;
        clearNearbyScan = false;
        destroyedMaterial = null;

        var normalizedText = EventLogLineNormalizer.Normalize(orderedEventLine.Line.Text);
        if (normalizedText == DarkDungeonsProspectingFailure
            && state.ActiveMiningAttempt is not { Phase: MiningAttemptPhase.Active or MiningAttemptPhase.WallBrokenAwaitingPrimary })
        {
            ClearState(state, "DarkDungeonsProspectingFailure", resetAssembler: false);
            clearReason = state.LastClearReason;
            return false;
        }

        if (normalizedText is EnterCave or LeaveCave)
        {
            ClearState(state, "CaveTransition");
            clearNearbyScan = true;
            clearReason = state.LastClearReason;
            return false;
        }

        if (ProspectingLineParser.TryParseAugment(orderedEventLine.Line.Text, out var augment))
        {
            if (TryUpgradeEligiblePlainRock(state, augment))
                return false;

            if (augment == ProspectingAugment.Salt
                && TryUpgradeEligibleDirectVein(state))
            {
                return false;
            }

            state.EligibleDirectVeinContext = null;
        }
        else
        {
            state.EligiblePlainRockResult = null;
            state.EligibleDirectVeinContext = null;
        }

        if (_sessionBoundaryParser.IsLoggingStarted(orderedEventLine.Line.Text)
                || _sessionBoundaryParser.IsReconnect(orderedEventLine.Line.Text))
        {
            ClearState(state, "SessionBoundary");
            clearReason = state.LastClearReason;
            return false;
        }

        if (ProspectingLineParser.TryParseNearbyScan(orderedEventLine.Line.Text, out _))
        {
            ClearState(state, "SurfaceProspecting");
            clearReason = state.LastClearReason;
            return false;
        }

        if (VeinEmergenceParser.TryParse(orderedEventLine.Line.Text, out var emergedMaterial))
        {
            ApplyVeinEmergence(state, emergedMaterial);
            return false;
        }

        var actionContext = _miningActionContextParser.Parse(orderedEventLine.Line.Text);
        if (actionContext is not null)
        {
            HandleActionStart(state, actionContext.Value);
            clearNearbyScan = actionContext == MiningActionContext.Ceiling;
            clearReason = state.LastClearReason;
            return false;
        }

        if (transition == MiningAttemptTransitionKind.WallBreak)
        {
            destroyedMaterial = HandleWallBreak(state);
            clearReason = state.LastClearReason;
            return false;
        }

        if (transition is MiningAttemptTransitionKind.InformationalWarning
            or MiningAttemptTransitionKind.WallBreakSoon)
        {
            return false;
        }

        if (transition == MiningAttemptTransitionKind.PreStartRejection)
        {
            return false;
        }

        if (transition == MiningAttemptTransitionKind.NoYieldTerminal)
        {
            HandleNoYieldTerminal(state);
            return false;
        }

        if (transition == MiningAttemptTransitionKind.ContextSensitiveNoYield)
        {
            HandleContextSensitiveNoYield(state, orderedEventLine.Line.Text);
            return false;
        }

        var miningEvent = _miningEventParser.Parse(orderedEventLine.Line.Text);
        if (miningEvent is not null)
        {
            var isLocalExactDecrement = HandleSuccessfulPrimary(state, miningEvent.Material);
            clearReason = state.LastClearReason;
            return isLocalExactDecrement;
        }

        var result = state.Assembler.Process(orderedEventLine.Line.Text);
        prospectingDiagnostic = state.Assembler.LastRecognizedEvent;
        if (state.Assembler.TryTakeObservationUpdate(out var observationUpdate))
        {
            ApplyMaterialObservation(state, observationUpdate);
            if (observationUpdate.IsSynchronized)
            {
                state.LastPlainRockResult = null;
                state.EligibleDirectVeinContext = result is ProspectingResult.Vein
                {
                    Material: not VeinMaterial.RockSalt,
                    Quality: QualityMeasurement.ExactMax,
                    Remaining: not RemainingMeasurement.Unknown
                }
                    ? state.ActiveContext
                    : null;
            }
            clearNearbyScan = observationUpdate.IsSynchronized;
            return false;
        }

        if (result is ProspectingResult.PlainRock plainRock)
        {
            state.ActiveContext = null;
            state.LastPlainRockResult = plainRock;
            state.EligiblePlainRockResult = plainRock.Quality is QualityMeasurement.Unknown
                ? null
                : plainRock;
            state.LastClearReason = "PlainRock";
            clearReason = state.LastClearReason;
            prospectingDiagnostic ??= new ProspectingSequenceDiagnostic(ProspectingSequenceEventKind.Failure);
            return false;
        }

        var examinedMaterial = _examineParser.Parse(orderedEventLine.Line.Text);
        if (examinedMaterial is not null)
            ApplyMaterialObservation(state, CreateMaterialOnlyObservation(examinedMaterial.Value));

        return false;
    }

    private static bool TryUpgradeEligiblePlainRock(
        ProfileVeinState state,
        ProspectingAugment augment)
    {
        var eligibleResult = state.EligiblePlainRockResult;
        if (eligibleResult is null)
            return false;

        if (eligibleResult.Augments.Contains(augment))
            return true;

        var replacement = eligibleResult with
        {
            Augments = eligibleResult.Augments.Add(augment)
        };
        state.LastPlainRockResult = replacement;
        state.EligiblePlainRockResult = replacement;
        return true;
    }

    private static bool TryUpgradeEligibleDirectVein(ProfileVeinState state)
    {
        var eligibleContext = state.EligibleDirectVeinContext;
        if (eligibleContext is null)
            return false;

        if (eligibleContext.Augments.Contains(ProspectingAugment.Salt))
            return true;

        var replacement = eligibleContext with
        {
            Augments = eligibleContext.Augments.Add(ProspectingAugment.Salt)
        };
        state.ActiveContext = replacement;
        state.EligibleDirectVeinContext = replacement;
        return true;
    }

    private static VeinConfidence GetObservationConfidence(ProspectingObservationUpdate update)
    {
        if (update.IsSynchronized)
            return VeinConfidence.Synchronized;

        return update.Quality is QualityMeasurement.Unknown
            ? VeinConfidence.TypeOnly
            : VeinConfidence.Partial;
    }

    private static void ApplyMaterialObservation(
        ProfileVeinState state,
        ProspectingObservationUpdate observationUpdate)
    {
        if (state.ActiveMiningAttempt?.BoundVeinContext is { } boundContext
            && boundContext.Material != observationUpdate.Material)
        {
            state.ActiveMiningAttempt = null;
        }

        if (state.ActiveContext?.Material == observationUpdate.Material
            && IsMaterialOnly(observationUpdate))
        {
            return;
        }

        state.ActiveContext = new ActiveVeinContext(
            observationUpdate.Material,
            observationUpdate.Quality,
            observationUpdate.Remaining,
            GetObservationConfidence(observationUpdate));
        state.LastClearReason = null;
    }

    private static ProspectingObservationUpdate CreateMaterialOnlyObservation(VeinMaterial material) =>
        new(
            material,
            new QualityMeasurement.Unknown(),
            new RemainingMeasurement.Unknown(),
            IsSynchronized: false);

    private static bool IsMaterialOnly(ProspectingObservationUpdate observationUpdate) =>
        observationUpdate.Quality is QualityMeasurement.Unknown
        && observationUpdate.Remaining is RemainingMeasurement.Unknown;

    private static void ApplyVeinEmergence(ProfileVeinState state, VeinMaterial material)
    {
        if (!state.Assembler.IsAwaitingFirstMaterial)
            state.Assembler.Reset();

        ApplyMaterialObservation(state, CreateMaterialOnlyObservation(material));
    }

    private void OnActiveContextChanged(VeinContextChangedEventArgs? contextChanged)
    {
        if (contextChanged is not null)
            ActiveContextChanged?.Invoke(this, contextChanged);
    }

    private void OnPlainRockProspectingResultChanged(
        PlainRockProspectingResultChangedEventArgs? resultChanged)
    {
        if (resultChanged is not null)
            PlainRockProspectingResultChanged?.Invoke(this, resultChanged);
    }

    private void ReportProspectingEvent(
        PlayerProfileId profileId,
        string profileName,
        ProspectingSequenceDiagnostic? diagnostic)
    {
        try
        {
            if (diagnostic is null)
                return;

            var details = diagnostic.Kind switch
            {
                ProspectingSequenceEventKind.Start => "Start",
                ProspectingSequenceEventKind.Stop => "Stop",
                ProspectingSequenceEventKind.Failure => "Failure=PlainRock",
                ProspectingSequenceEventKind.Material => $"Material={diagnostic.Material}",
                ProspectingSequenceEventKind.Quality => $"Quality={FormatQuality(diagnostic.Quality)}",
                ProspectingSequenceEventKind.Remaining => $"Remaining={FormatRemaining(diagnostic.Remaining)}",
                _ => "Unknown"
            };
            _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Info,
                nameof(VeinContextProcessor),
                profileName,
                "Prospecting",
                details);
        }
        catch
        {
            // Diagnostics must not affect the ordered Prospecting pipeline.
        }
    }

    private void ReportContextChange(
        string profileName,
        VeinContextChangedEventArgs? contextChanged,
        string? clearReason)
    {
        try
        {
            if (contextChanged is null || contextChanged.IsLocalExactDecrement)
                return;

            var isUpdate = contextChanged.Snapshot is not null;
            var details = contextChanged.Snapshot is { } snapshot
                ? $"Material={snapshot.Material} | Quality={FormatQuality(snapshot.Quality)} | Remaining={FormatRemaining(snapshot.Remaining)} | Confidence={snapshot.Confidence}"
                : $"Cleared: {clearReason ?? "Unknown"}";
            _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Info,
                nameof(VeinContextProcessor),
                profileName,
                isUpdate ? "VeinContextUpdate" : "VeinContext",
                details);
        }
        catch
        {
            // Diagnostics must not affect the ordered Prospecting pipeline.
        }
    }

    private static string FormatQuality(QualityMeasurement? quality) => quality switch
    {
        QualityMeasurement.ExactMax exact => $"Max {exact.Value}",
        QualityMeasurement.Grade grade => grade.Value.ToString(),
        _ => "Unknown"
    };

    private static string FormatRemaining(RemainingMeasurement? remaining) => remaining switch
    {
        RemainingMeasurement.Exact exact => exact.Value.ToString(),
        RemainingMeasurement.EstimateBand estimateBand => estimateBand.Value.ToString(),
        _ => "Unknown"
    };

    private static VeinContextChangedEventArgs? CreateContextChangedEventArgs(
        PlayerProfileId profileId,
        string profileName,
        ActiveVeinContext? previousContext,
        ActiveVeinContext? currentContext,
        bool isLocalExactDecrement = false)
    {
        if (EqualityComparer<ActiveVeinContext?>.Default.Equals(previousContext, currentContext))
            return null;

        return new VeinContextChangedEventArgs(
            profileId,
            currentContext is null ? null : CreateSnapshot(profileId, profileName, currentContext),
            isLocalExactDecrement);
    }

    private static PlainRockProspectingResultChangedEventArgs? CreatePlainRockResultChangedEventArgs(
        PlayerProfileId profileId,
        string profileName,
        ProspectingResult.PlainRock? previousResult,
        ProspectingResult.PlainRock? currentResult)
    {
        return EqualityComparer<ProspectingResult.PlainRock?>.Default.Equals(previousResult, currentResult)
            ? null
            : new PlainRockProspectingResultChangedEventArgs(profileId, profileName, currentResult);
    }

    private static VeinContextSnapshot CreateSnapshot(
        PlayerProfileId profileId,
        string profileName,
        ActiveVeinContext context)
    {
        return new VeinContextSnapshot(
            profileId,
            profileName,
            context.Material,
            context.Quality,
            context.Remaining,
            context.Confidence)
        {
            Augments = context.Augments
        };
    }

    private static VeinMaterial? HandleWallBreak(ProfileVeinState state)
    {
        var attempt = state.ActiveMiningAttempt;
        if (attempt is not
            {
                ActionContext: MiningActionContext.Wall,
                Phase: MiningAttemptPhase.Active
            })
        {
            return null;
        }

        var boundContext = attempt.BoundVeinContext;
        if (boundContext is not null && !ReferenceEquals(state.ActiveContext, boundContext))
        {
            state.ActiveMiningAttempt = null;
            return null;
        }

        state.ActiveMiningAttempt = attempt with
        {
            Phase = MiningAttemptPhase.WallBrokenAwaitingPrimary
        };

        if (boundContext is null)
            return null;

        state.Assembler.Reset();
        state.ActiveContext = null;
        state.LastPlainRockResult = null;
        state.EligiblePlainRockResult = null;
        state.EligibleDirectVeinContext = null;
        state.LastClearReason = "WallBreak";
        return boundContext.Material;
    }

    private static void HandleNoYieldTerminal(ProfileVeinState state)
    {
        var attempt = state.ActiveMiningAttempt;
        if (attempt is null || attempt.Phase == MiningAttemptPhase.Completed)
        {
            return;
        }

        state.ActiveMiningAttempt = attempt with
        {
            Phase = MiningAttemptPhase.Completed
        };
    }

    private static void HandleContextSensitiveNoYield(ProfileVeinState state, string line)
    {
        var attempt = state.ActiveMiningAttempt;
        if (attempt is null || attempt.Phase != MiningAttemptPhase.Active)
        {
            return;
        }

        var normalizedLine = EventLogLineNormalizer.Normalize(line);
        var isTerminal = normalizedLine switch
        {
            ChipAwayAtRock => attempt.ActionContext is MiningActionContext.Surface or MiningActionContext.Tunnelling,
            SurfaceTooFlat => attempt.ActionContext == MiningActionContext.Surface,
            _ => false
        };

        if (!isTerminal)
        {
            return;
        }

        state.ActiveMiningAttempt = attempt with
        {
            Phase = MiningAttemptPhase.Completed
        };
    }

    private static bool HandleSuccessfulPrimary(ProfileVeinState state, MiningMaterial material)
    {
        if (!material.IsPrimaryForCounter())
        {
            return false;
        }

        var attempt = state.ActiveMiningAttempt;
        if (attempt is null || attempt.Phase == MiningAttemptPhase.Completed)
        {
            return false;
        }

        if (attempt.ActionContext == MiningActionContext.Wall
            && attempt.BoundVeinContext is not null
            && !IsCompatibleWallPrimary(attempt, material))
        {
            if (attempt.Phase == MiningAttemptPhase.WallBrokenAwaitingPrimary)
                state.ActiveMiningAttempt = null;
            else
                ClearState(state, "IncompatibleMiningYield");
            return false;
        }

        var shouldComplete = attempt.ActionContext switch
        {
            MiningActionContext.Floor or
            MiningActionContext.Ceiling or
            MiningActionContext.Surface or
            MiningActionContext.Tunnelling => true,
            MiningActionContext.Wall => attempt.BoundVeinContext is null || IsCompatibleWallPrimary(attempt, material),
            _ => false
        };

        if (!shouldComplete)
        {
            return false;
        }

        if (attempt.Phase == MiningAttemptPhase.WallBrokenAwaitingPrimary)
        {
            state.ActiveMiningAttempt = null;
            return false;
        }

        var isLocalExactDecrement = TryDecrementRemaining(state, attempt, material);

        state.ActiveMiningAttempt = attempt with
        {
            Phase = MiningAttemptPhase.Completed
        };

        return isLocalExactDecrement;
    }

    private static bool IsCompatibleWallPrimary(MiningAttempt attempt, MiningMaterial material)
    {
        return attempt.BoundVeinContext is { } boundVeinContext
            && material.ToDepletionCompatibleYieldKind() == boundVeinContext.Material.ToYieldKind();
    }

    private static bool TryDecrementRemaining(
        ProfileVeinState state,
        MiningAttempt attempt,
        MiningMaterial material)
    {
        if (attempt.ActionContext != MiningActionContext.Wall
            || attempt.BoundVeinContext is null
            || !IsCompatibleWallPrimary(attempt, material)
            || !ReferenceEquals(state.ActiveContext, attempt.BoundVeinContext)
            || state.ActiveContext?.Remaining is not RemainingMeasurement.Exact { Value: > 0 } remaining)
        {
            return false;
        }

        state.ActiveContext = state.ActiveContext with
        {
            Remaining = new RemainingMeasurement.Exact(remaining.Value - 1),
            Confidence = VeinConfidence.Tracking
        };

        return true;
    }

    private static void HandleActionStart(ProfileVeinState state, MiningActionContext actionContext)
    {
        switch (actionContext)
        {
            case MiningActionContext.Wall:
                state.ActiveMiningAttempt = new MiningAttempt(
                    actionContext,
                    IsQualifiedForWallBinding(state.ActiveContext) ? state.ActiveContext : null,
                    MiningAttemptPhase.Active);
                return;

            case MiningActionContext.Floor:
            case MiningActionContext.Ceiling:
                state.ActiveMiningAttempt = new MiningAttempt(
                    actionContext,
                    null,
                    MiningAttemptPhase.Active);
                return;

            case MiningActionContext.Surface:
            case MiningActionContext.Tunnelling:
                ClearState(state, actionContext == MiningActionContext.Surface
                    ? "SurfaceProspecting"
                    : "Tunnelling");
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(actionContext), actionContext, null);
        }
    }

    private static bool IsQualifiedForWallBinding(ActiveVeinContext? context)
    {
        return context is
        {
            Confidence: VeinConfidence.Synchronized or VeinConfidence.Tracking,
            Quality: QualityMeasurement.ExactMax,
            Remaining: RemainingMeasurement.Exact { Value: > 0 }
        };
    }

    private void ClearProfileState(PlayerProfileId profileId, string reason)
    {
        if (!_profileStates.TryGetValue(profileId, out var state))
        {
            return;
        }

        VeinContextChangedEventArgs? contextChanged;
        PlainRockProspectingResultChangedEventArgs? plainRockResultChanged;
        string profileName;
        lock (state.Sync)
        {
            profileName = state.ProfileName;
            var previousContext = state.ActiveContext;
            var previousPlainRockResult = state.LastPlainRockResult;
            ClearState(state, reason);
            contextChanged = CreateContextChangedEventArgs(
                profileId,
                profileName,
                previousContext,
                state.ActiveContext);
            plainRockResultChanged = CreatePlainRockResultChangedEventArgs(
                profileId,
                profileName,
                previousPlainRockResult,
                state.LastPlainRockResult);
        }

        ReportContextChange(profileName, contextChanged, reason);
        OnActiveContextChanged(contextChanged);
        OnPlainRockProspectingResultChanged(plainRockResultChanged);
    }

    private static void ClearState(ProfileVeinState state, string reason, bool resetAssembler = true)
    {
        if (resetAssembler)
            state.Assembler.Reset();
        state.ActiveContext = null;
        state.LastPlainRockResult = null;
        state.EligiblePlainRockResult = null;
        state.EligibleDirectVeinContext = null;
        state.ActiveMiningAttempt = null;
        state.LastClearReason = reason;
    }

    private sealed class ProfileVeinState
    {
        public ProfileVeinState(string profileName) => ProfileName = profileName;

        public string ProfileName { get; }

        public object Sync { get; } = new();

        public ProspectingSequenceAssembler Assembler { get; } = new();

        public ActiveVeinContext? ActiveContext { get; set; }

        public ProspectingResult.PlainRock? LastPlainRockResult { get; set; }

        public ProspectingResult.PlainRock? EligiblePlainRockResult { get; set; }

        public ActiveVeinContext? EligibleDirectVeinContext { get; set; }

        public MiningAttempt? ActiveMiningAttempt { get; set; }

        public string? LastClearReason { get; set; }
    }
}
