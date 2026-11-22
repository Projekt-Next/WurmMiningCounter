using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertCatalogTests
{
    [Fact]
    public void StartsWithTheTwoExactBuiltInTriggers()
    {
        var triggers = new EventAlertCatalog().GetSnapshot();

        Assert.Equal(2, triggers.Length);
        Assert.Equal(
            [EventAlertBuiltIns.WallBreakSoonId, EventAlertBuiltIns.WallBreakId],
            triggers.Select(trigger => trigger.Id));
        Assert.All(triggers, trigger => Assert.True(trigger.IsBuiltIn));
        Assert.Equal(EventLogMessages.WallBreakSoon, triggers[0].MatchText);
        Assert.Null(triggers[0].SoundFileName);
        Assert.Equal(EventLogMessages.WallBreak, triggers[1].MatchText);
        Assert.Null(triggers[1].SoundFileName);
    }

    [Fact]
    public void BuiltInTriggerCanBeEnabledAndDisabledOnly()
    {
        var catalog = new EventAlertCatalog();

        catalog.SetEnabled(EventAlertBuiltIns.WallBreakSoonId, false);
        Assert.False(catalog.GetSnapshot().Single(trigger =>
            trigger.Id == EventAlertBuiltIns.WallBreakSoonId).Enabled);

        catalog.SetEnabled(EventAlertBuiltIns.WallBreakSoonId, true);
        Assert.True(catalog.GetSnapshot().Single(trigger =>
            trigger.Id == EventAlertBuiltIns.WallBreakSoonId).Enabled);

        var builtIn = catalog.GetSnapshot()[0];
        Assert.Throws<ArgumentException>(() => catalog.EditCustom(builtIn));
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteCustom(builtIn.Id));
    }

    [Fact]
    public void CustomTriggerSupportsCrud()
    {
        var catalog = new EventAlertCatalog();
        var custom = CustomTrigger();

        catalog.AddCustom(custom);
        Assert.Contains(custom, catalog.GetSnapshot());

        var edited = custom with
        {
            Name = "Edited",
            MatchText = "Edited text",
            Enabled = false,
            SoundFileName = null,
            PopupText = "Edited popup"
        };
        catalog.EditCustom(edited);
        Assert.Equal(edited, Assert.Single(catalog.GetSnapshot().Where(trigger => trigger.Id == custom.Id)));

        catalog.SetEnabled(custom.Id, true);
        Assert.True(catalog.GetSnapshot().Single(trigger => trigger.Id == custom.Id).Enabled);

        catalog.DeleteCustom(custom.Id);
        Assert.DoesNotContain(catalog.GetSnapshot(), trigger => trigger.Id == custom.Id);
    }

    [Fact]
    public void SnapshotIsImmutableAndCannotChangeTheCatalog()
    {
        var catalog = new EventAlertCatalog();
        var snapshot = catalog.GetSnapshot();
        var altered = snapshot.SetItem(0, snapshot[0] with { Enabled = false });

        Assert.True(catalog.GetSnapshot()[0].Enabled);
        Assert.False(altered[0].Enabled);
    }

    [Fact]
    public void RejectsDuplicateAndInvalidCustomTriggers()
    {
        var catalog = new EventAlertCatalog();
        var custom = CustomTrigger();
        catalog.AddCustom(custom);

        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with { Id = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with { Id = Guid.NewGuid(), Name = " " }));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with { Id = Guid.NewGuid(), MatchText = " " }));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with { Id = Guid.NewGuid(), MatchText = "or" }));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with
        {
            Id = EventAlertBuiltIns.WallBreakId,
            IsBuiltIn = false
        }));
        Assert.Throws<ArgumentException>(() => catalog.AddCustom(custom with
        {
            Id = Guid.NewGuid(),
            IsBuiltIn = true
        }));
    }

    private static EventAlertTrigger CustomTrigger() => new(
        Guid.NewGuid(),
        "Custom",
        "Custom line",
        Enabled: true,
        null,
        "Custom popup",
        IsBuiltIn: false);
}
