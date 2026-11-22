using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests;

/// <summary>Explicit identities for Event transport test fixtures.</summary>
internal static class EventTransportTestProfileIds
{
    internal static readonly PlayerProfileId Alice = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId Bob = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId Charlie = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId Default = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId ProfileA = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId ProfileALower = PlayerProfileId.CreateNew();
    internal static readonly PlayerProfileId ProfileB = PlayerProfileId.CreateNew();

    internal static PlayerProfileId For(string profileName) => profileName switch
    {
        "Alice" => Alice,
        "Bob" => Bob,
        "Charlie" => Charlie,
        _ => Default
    };
}
