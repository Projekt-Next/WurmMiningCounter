namespace WurmMiningCounter.Models;

/// <summary>
/// Opaque, application-owned identity for one discovered player profile.
/// It is deliberately unrelated to the display name, a directory path, or a
/// log file generation.
/// </summary>
public readonly record struct PlayerProfileId
{
    private readonly Guid _value;

    internal PlayerProfileId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A player profile identity cannot be empty.", nameof(value));

        _value = value;
    }

    internal static PlayerProfileId CreateNew() => new(Guid.NewGuid());

    internal static bool TryParse(string? value, out PlayerProfileId id)
    {
        if (Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty)
        {
            id = new PlayerProfileId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => _value.ToString("N");
}

public enum PlayerProfileIdentityConfidence
{
    ConfirmedPhysicalIdentity,
    UnconfirmedPathIdentity
}

/// <summary>
/// A profile identity together with the evidence used for this resolution.
/// Unconfirmed identities are intentionally never merged with another path.
/// </summary>
public sealed record ResolvedPlayerProfileIdentity(
    PlayerProfileId Id,
    string CanonicalProfileDirectoryPath,
    PlayerProfileIdentityConfidence Confidence);
