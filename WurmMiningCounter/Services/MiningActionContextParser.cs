using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningActionContextParser
{
    private static readonly IReadOnlyDictionary<string, MiningActionContext> ContextsByStartMessage =
        new Dictionary<string, MiningActionContext>(StringComparer.Ordinal)
        {
            ["You start to mine forwards."] = MiningActionContext.Wall,
            ["You start to mine upwards."] = MiningActionContext.Wall,
            ["You start to mine up."] = MiningActionContext.Wall,
            ["You start to mine downwards."] = MiningActionContext.Wall,
            ["You start to mine down."] = MiningActionContext.Wall,
            ["You start to mine the floor."] = MiningActionContext.Floor,
            ["You start to mine the ceiling."] = MiningActionContext.Ceiling,
            ["You start to mine."] = MiningActionContext.Surface,
            ["You start to tunnel."] = MiningActionContext.Tunnelling
        };

    public MiningActionContext? Parse(string line)
    {
        var normalizedLine = EventLogLineNormalizer.Normalize(line);

        return ContextsByStartMessage.TryGetValue(normalizedLine, out var context)
            ? context
            : null;
    }
}
