using System.IO;

namespace WurmMiningCounter.Services;

public sealed class PlayerDataDirectoryLocator
{
    public bool TryGetPlayersDirectoryPath(out string playersDirectoryPath)
    {
        playersDirectoryPath = string.Empty;

        try
        {
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(userProfilePath, "wurm", "players");
            if (!Directory.Exists(path))
                return false;

            playersDirectoryPath = path;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
