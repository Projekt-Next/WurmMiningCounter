using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class ExternalSoundServiceTests
{
    [Fact]
    public void UsesSoundsDirectoryUnderApplicationBaseDirectory()
    {
        var service = new ExternalSoundService();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Sounds"),
            ExternalSoundService.SoundsDirectoryPath);
    }

    [Fact]
    public void MissingFilesDoNotReachPlayback()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var playbackCalls = 0;
        var service = new ExternalSoundService(baseDirectory, _ => playbackCalls++);

        service.Play("custom.wav");

        Assert.Equal(0, playbackCalls);
    }

    [Fact]
    public void PlaybackFailureDoesNotReachCaller()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var soundsDirectory = Path.Combine(baseDirectory, "Sounds");
        Directory.CreateDirectory(soundsDirectory);
        try
        {
            var soundPath = Path.Combine(soundsDirectory, "custom.wav");
            File.WriteAllBytes(soundPath, []);
            var playbackCalls = 0;
            var service = new ExternalSoundService(baseDirectory, path =>
            {
                Assert.Equal(soundPath, path);
                playbackCalls++;
                throw new InvalidOperationException("Invalid audio data.");
            });

            service.Play("custom.wav");

            Assert.Equal(1, playbackCalls);
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\x.wav")]
    [InlineData("subdir\\x.wav")]
    [InlineData("C:\\outside.wav")]
    [InlineData("custom.mp3")]
    public void UnsafeSoundFileNameCannotEscapeSoundsDirectory(string soundFileName)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new ExternalSoundService(baseDirectory, _ => throw new InvalidOperationException("Must not play."));

        Assert.False(service.TryGetSoundPath(soundFileName, out _));
    }
}
