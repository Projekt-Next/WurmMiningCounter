namespace WurmMiningCounter.Models;

/// <summary>One exact Event log line matched an enabled alert trigger.</summary>
public sealed record EventAlertNotification(string ProfileName, EventAlertTrigger Trigger);
