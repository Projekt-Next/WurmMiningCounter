using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class ProspectingDiagnosticsTests
{
    [Fact]
    public void RecognizedProspectingSequenceProducesDomainDiagnostics()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var processor = new VeinContextProcessor(diagnostics);

        ProcessLine(processor, 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, 2, "You would mine iron ore here.");
        ProcessLine(processor, 3, "It has a max quality of 72.");
        ProcessLine(processor, 4, "You will be able to mine here 1450 more times.");

        var messages = diagnostics.GetSnapshot().Select(entry => entry.Message).ToArray();

        Assert.Contains("ProfileA | Prospecting | Start", messages);
        Assert.Contains("ProfileA | Prospecting | Material=Iron", messages);
        Assert.Contains("ProfileA | Prospecting | Quality=Max 72", messages);
        Assert.Contains("ProfileA | Prospecting | Remaining=1450", messages);
        Assert.Contains("ProfileA | VeinContextUpdate | Material=Iron | Quality=Max 72 | Remaining=1450 | Confidence=Synchronized", messages);
    }

    [Fact]
    public void PartialProspectingContextUsesExplicitUnknownRemainingDiagnostic()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var processor = new VeinContextProcessor(diagnostics);

        ProcessLine(processor, 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, 2, "You would mine sandstone shards here.");
        ProcessLine(processor, 3, "It has a max quality of 87.");

        var messages = diagnostics.GetSnapshot().Select(entry => entry.Message).ToArray();

        Assert.Contains("ProfileA | VeinContextUpdate | Material=Sandstone | Quality=Max 87 | Remaining=Unknown | Confidence=Partial", messages);
    }

    [Fact]
    public void NearbyScanAndContextClearProduceDomainDiagnostics()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var veinContextProcessor = new VeinContextProcessor(diagnostics);
        var nearbyScanProcessor = new NearbyScanProcessor(diagnostics);

        ProcessLine(veinContextProcessor, 1, "You start to gather fragments of the rock.");
        ProcessLine(veinContextProcessor, 2, "You would mine iron ore here.");
        ProcessLine(veinContextProcessor, 3, "There is iron, copper, tin and lead nearby.");
        nearbyScanProcessor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.ProfileA,
            "ProfileA",
            3,
            new EventLogLine(EventTransportTestProfileIds.ProfileA, "ProfileA", "event.txt", "There is iron, copper, tin and lead nearby.")));

        var messages = diagnostics.GetSnapshot().Select(entry => entry.Message).ToArray();

        Assert.Contains("ProfileA | VeinContext | Cleared: SurfaceProspecting", messages);
        Assert.Contains("ProfileA | NearbyScan | Entries=4", messages);
    }

    private static void ProcessLine(VeinContextProcessor processor, long sequenceNumber, string text) =>
        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.ProfileA,
            "ProfileA",
            sequenceNumber,
            new EventLogLine(EventTransportTestProfileIds.ProfileA, "ProfileA", "event.txt", text)));
}
