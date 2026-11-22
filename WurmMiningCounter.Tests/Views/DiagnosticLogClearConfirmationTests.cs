using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

public sealed class DiagnosticLogClearConfirmationTests
{
    [Fact]
    public void ClearIfConfirmedClearsDiagnostics()
    {
        var diagnostics = CreateDiagnosticsWithEntry();

        var cleared = DiagnosticLogClearConfirmation.ClearIfConfirmed(() => true, diagnostics.Clear);

        Assert.True(cleared);
        Assert.Empty(diagnostics.GetSnapshot());
    }

    [Fact]
    public void ClearIfConfirmedDoesNothingWhenCancelled()
    {
        var diagnostics = CreateDiagnosticsWithEntry();

        var cleared = DiagnosticLogClearConfirmation.ClearIfConfirmed(() => false, diagnostics.Clear);

        Assert.False(cleared);
        Assert.Single(diagnostics.GetSnapshot());
    }

    private static RuntimeDiagnosticService CreateDiagnosticsWithEntry()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Entry");
        return diagnostics;
    }
}
