using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertModuleTests
{
    [Theory]
    [InlineData("Alice", EventLogMessages.WallBreakSoon, "5C61B657-516B-433A-9C7F-040B6D6C9197")]
    [InlineData("Bob", EventLogMessages.WallBreak, "CB06F5AB-05EF-4CF6-8590-6E8675988948")]
    public void BuiltInWallBreakTriggersEmitMatchingNotification(
        string profileName,
        string text,
        string triggerId)
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(Line(profileName, text));

        var notification = Assert.Single(notifications);
        Assert.Equal(profileName, notification.ProfileName);
        Assert.Equal(Guid.Parse(triggerId), notification.Trigger.Id);
    }

    [Theory]
    [InlineData("[12:00:00] The wall breaks!", "CB06F5AB-05EF-4CF6-8590-6E8675988948")]
    [InlineData("[12:34:56] The wall will break soon.", "5C61B657-516B-433A-9C7F-040B6D6C9197")]
    public void TimestampedBuiltInLineEmitsExactlyOneNotification(string rawText, string triggerId)
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(Line("Alice", rawText));

        var notification = Assert.Single(notifications);
        Assert.Equal(Guid.Parse(triggerId), notification.Trigger.Id);
    }

    [Fact]
    public void CustomTriggerEmitsMatchingNotification()
    {
        var catalog = new EventAlertCatalog();
        var custom = CustomTrigger("Custom text");
        catalog.AddCustom(custom);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(catalog, notifications.Add);

        module.Process(Line("Alice", custom.MatchText));

        Assert.Equal(new EventAlertNotification("Alice", custom), Assert.Single(notifications));
    }

    [Fact]
    public void TimestampedCustomLineEmitsExactlyOneNotification()
    {
        const string matchText = "You mine some iron ore.";
        var catalog = new EventAlertCatalog();
        var custom = CustomTrigger(matchText);
        catalog.AddCustom(custom);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(catalog, notifications.Add);

        module.Process(Line("Alice", "[12:34:56] You mine some iron ore."));

        Assert.Equal(new EventAlertNotification("Alice", custom), Assert.Single(notifications));
    }

    [Theory]
    [InlineData("shards", "You mine some shards.")]
    [InlineData("mine some", "You mine some shards.")]
    [InlineData("some ore", "You mine some ore.")]
    [InlineData("MINE SOME", "You mine some ore.")]
    [InlineData("ore", "You mine some ore.")]
    [InlineData("You mine some ore", "You mine some ore.")]
    public void CustomTriggerMatchesWholeWordsOrContiguousPhrases(string pattern, string text)
    {
        var custom = CustomTrigger(pattern);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog([.. EventAlertBuiltIns.Create(), custom]), notifications.Add);

        module.Process(Line("Alice", text));

        Assert.Equal(custom.Id, Assert.Single(notifications).Trigger.Id);
    }

    [Theory]
    [InlineData("ore", "before")]
    [InlineData("ore", "ores")]
    [InlineData("mine ore", "You mine some ore.")]
    public void CustomTriggerDoesNotMatchWordFragmentsOrNonContiguousPhrases(string pattern, string text)
    {
        var custom = CustomTrigger(pattern);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog([.. EventAlertBuiltIns.Create(), custom]), notifications.Add);

        module.Process(Line("Alice", text));

        Assert.Empty(notifications);
    }

    [Fact]
    public void TimestampedCustomPhraseEmitsExactlyOnce()
    {
        var custom = CustomTrigger("mine some");
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog([.. EventAlertBuiltIns.Create(), custom]), notifications.Add);

        module.Process(Line("Alice", "[19:09:07] You mine some shards."));

        Assert.Equal(custom.Id, Assert.Single(notifications).Trigger.Id);
    }

    [Fact]
    public void DisabledCustomTriggerDoesNotEmit()
    {
        var custom = CustomTrigger("mine some") with { Enabled = false };
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog([.. EventAlertBuiltIns.Create(), custom]), notifications.Add);

        module.Process(Line("Alice", "You mine some ore."));

        Assert.Empty(notifications);
    }

    [Fact]
    public void DisabledTriggerDoesNotEmit()
    {
        var catalog = new EventAlertCatalog();
        catalog.SetEnabled(EventAlertBuiltIns.WallBreakSoonId, false);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(catalog, notifications.Add);

        module.Process(Line("Alice", EventLogMessages.WallBreakSoon));

        Assert.Empty(notifications);
    }

    [Fact]
    public void SimilarButNonExactLineDoesNotMatch()
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(Line("Alice", EventLogMessages.WallBreak + " now"));

        Assert.Empty(notifications);
    }

    [Fact]
    public void TimestampedDifferentTextDoesNotMatchAfterNormalization()
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(Line("Alice", "[12:00:00] The wall breaks! now"));

        Assert.Empty(notifications);
    }

    [Fact]
    public void TimestampedMatchRemainsOrdinalAfterNormalization()
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(Line("Alice", "[12:00:00] the wall breaks!"));

        Assert.Empty(notifications);
    }

    [Fact]
    public void ProfilesEmitIndependently()
    {
        var notifications = new ConcurrentQueue<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Enqueue);

        module.Process(Line("Alice", EventLogMessages.WallBreak));
        module.Process(Line("Bob", EventLogMessages.WallBreak));

        Assert.Equal(["Alice", "Bob"], notifications.Select(notification => notification.ProfileName));
    }

    [Fact]
    public void ResetAndContinuityDoNotEmitAndInvalidationIsNoOp()
    {
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog(), notifications.Add);

        module.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 1, StreamGapReason.RotationOrderLost, "Event.txt"));
        module.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 2));
        module.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        Assert.Empty(notifications);
    }

    [Fact]
    public void DuplicateCustomMatchTextEmitsEveryMatchingTriggerInCatalogOrder()
    {
        var first = CustomTrigger("same") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var second = CustomTrigger("same") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var catalog = new EventAlertCatalog([.. EventAlertBuiltIns.Create(), second, first]);
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(catalog, notifications.Add);

        module.Process(Line("Alice", "same"));

        Assert.Equal([first.Id, second.Id], notifications.Select(notification => notification.Trigger.Id));
    }

    [Fact]
    public void PersistedShortCustomPatternDoesNotMatch()
    {
        var legacy = CustomTrigger("a");
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(new EventAlertCatalog([.. EventAlertBuiltIns.Create(), legacy]), notifications.Add);

        module.Process(Line("Alice", "a"));

        Assert.Empty(notifications);
    }

    [Fact]
    public async Task SinkFailureDoesNotFaultTheContextPath()
    {
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(new EventAlertModule(
            new EventAlertCatalog(),
            _ => throw new InvalidOperationException("Alert sink failed.")));

        coordinator.AcceptOutput(new EventReadBatch(
            EventTransportTestProfileIds.Alice, "Alice", "Event.txt", 1, [EventLogMessages.WallBreakSoon]));

        await coordinator.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static OrderedEventLine Line(string profileName, string text) =>
        new(EventTransportTestProfileIds.For(profileName), profileName, 1, new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text));

    private static EventAlertTrigger CustomTrigger(string matchText) => new(
        Guid.NewGuid(),
        "Custom",
        matchText,
        Enabled: true,
        null,
        "Custom popup",
        IsBuiltIn: false);
}
