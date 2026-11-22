using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public interface IOrderedEventConsumer
{
    void Process(OrderedEventItem item);

    void Invalidate(PlayerProfileId profileId, string profileName);
}
