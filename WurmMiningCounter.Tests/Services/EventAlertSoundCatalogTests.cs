using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertSoundCatalogTests
{
    [Fact]
    public void ReturnsOnlyTopLevelWavFilesInDeterministicOrder()
    {
        using var fixture = new SoundDirectoryFixture();
        fixture.Write("b.wav");
        fixture.Write("a.wav");
        fixture.Write("note.txt");
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "nested"));
        File.WriteAllBytes(Path.Combine(fixture.DirectoryPath, "nested", "hidden.wav"), []);

        var catalog = new EventAlertSoundCatalog(fixture.DirectoryPath);

        Assert.Equal(["a.wav", "b.wav"], catalog.GetSnapshot());
    }

    [Fact]
    public void MissingDirectoryReturnsEmptySnapshot()
    {
        var catalog = new EventAlertSoundCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Empty(catalog.GetSnapshot());
    }

    [Fact]
    public void EmptyDirectoryReturnsEmptySnapshotWithoutError()
    {
        using var fixture = new SoundDirectoryFixture();

        var catalog = new EventAlertSoundCatalog(fixture.DirectoryPath);

        Assert.Empty(catalog.GetSnapshot());
    }

    [Fact]
    public void SubsequentSnapshotSeesNewWavFile()
    {
        using var fixture = new SoundDirectoryFixture();
        fixture.Write("first.wav");
        var catalog = new EventAlertSoundCatalog(fixture.DirectoryPath);

        Assert.Equal(["first.wav"], catalog.GetSnapshot());

        fixture.Write("second.wav");

        Assert.Equal(["first.wav", "second.wav"], catalog.GetSnapshot());
    }

    private sealed class SoundDirectoryFixture : IDisposable
    {
        public SoundDirectoryFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Write(string fileName) => File.WriteAllBytes(Path.Combine(DirectoryPath, fileName), []);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
