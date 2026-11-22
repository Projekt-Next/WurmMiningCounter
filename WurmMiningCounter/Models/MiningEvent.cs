namespace WurmMiningCounter.Models;

public sealed class MiningEvent
{
    public MiningEvent(MiningMaterial material)
    {
        Material = material;
    }

    public MiningMaterial Material { get; }
}
