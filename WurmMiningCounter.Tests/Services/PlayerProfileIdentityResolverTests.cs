using System.Text;
using System.Text.Json;
using System.Diagnostics;
using WurmMiningCounter;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class PlayerProfileIdentityResolverTests
{
    [Fact]
    public void InvalidRegistryBlocksActivationAndReportsIdentityErrorWithoutPathDiagnostics()
    {
        using var fixture = new RegistryFixture();
        var profilePath = fixture.Path("players", "Alice");
        Directory.CreateDirectory(profilePath);
        File.WriteAllText(fixture.RegistryFilePath, "{ invalid");
        var resolver = fixture.CreateResolver(_ => "volume:alice");
        var diagnostics = new RuntimeDiagnosticService();
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        App.WireIdentityActivationUiHealth(resolver, viewModel, action => action(), diagnostics);

        Assert.False(resolver.TryRegisterSource(profilePath, out var owner, out var readGuard));

        Assert.Null(owner);
        Assert.Null(readGuard);
        Assert.Equal(PlayerProfileIdentityActivationFailureReason.RegistryInvalid,
            Assert.Single(resolver.GetActivationStatusSnapshot()).FailureReason);
        Assert.Equal("Monitoring identity error", viewModel.StatusText);
        var diagnostic = Assert.Single(diagnostics.GetSnapshot());
        Assert.DoesNotContain(profilePath, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{ invalid", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableRegistryIsReportedSeparatelyFromInvalidRegistry()
    {
        using var fixture = new RegistryFixture();
        var profilePath = fixture.Path("players", "Alice");
        var registryDirectoryPath = fixture.Path("registry-directory-is-a-file");
        Directory.CreateDirectory(profilePath);
        File.WriteAllText(registryDirectoryPath, "not a directory");
        var resolver = new PlayerProfileIdentityResolver(
            registryDirectoryPath,
            new DelegateDirectoryPhysicalIdentityProvider(_ => "volume:alice"));

        Assert.False(resolver.TryRegisterSource(profilePath, out var owner, out var readGuard));

        Assert.Null(owner);
        Assert.Null(readGuard);
        Assert.Equal(PlayerProfileIdentityActivationFailureReason.RegistryUnavailable,
            Assert.Single(resolver.GetActivationStatusSnapshot()).FailureReason);
    }

    [Fact]
    public void HealthySourceRemainsActiveWhenAnotherProfileIsBlockedByFallbackReactivationRule()
    {
        using var fixture = new RegistryFixture();
        var alicePath = fixture.Path("players", "Alice");
        var bobPath = fixture.Path("players", "Bob");
        Directory.CreateDirectory(alicePath);
        Directory.CreateDirectory(bobPath);
        var resolver = fixture.CreateResolver(path => path == alicePath ? "volume:alice" : null);

        Assert.True(resolver.TryRegisterSource(bobPath, out var previousBobOwner, out var previousBobRead));
        previousBobRead!.Dispose();
        previousBobOwner!.Dispose();
        Assert.False(resolver.TryRegisterSource(bobPath, out var blockedBobOwner, out var blockedBobRead));
        Assert.Null(blockedBobOwner);
        Assert.Null(blockedBobRead);
        Assert.True(resolver.TryRegisterSource(alicePath, out var aliceOwner, out var aliceRead));
        using var activeAliceOwner = aliceOwner!;
        aliceRead!.Dispose();
        activeAliceOwner.MarkActivated();

        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        App.ApplyIdentityActivationUiHealth(resolver, viewModel);

        Assert.Equal("Monitoring identity warning", viewModel.StatusText);
        Assert.Contains(resolver.GetActivationStatusSnapshot(), status =>
            status.ProfileDirectoryPath == alicePath && status.IsActive);
        Assert.Contains(resolver.GetActivationStatusSnapshot(), status =>
            status.ProfileDirectoryPath == bobPath
            && status.FailureReason == PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked);
    }

    [Fact]
    public void MissingProfileDirectoryDoesNotRaiseIdentityAlarmAndSafeRegistrationClearsEarlierBlock()
    {
        using var fixture = new RegistryFixture();
        var profilePath = fixture.Path("players", "Alice");
        var resolver = fixture.CreateResolver(_ => "volume:alice");
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");

        App.ApplyIdentityActivationUiHealth(resolver, viewModel);
        Assert.Equal("Monitoring", viewModel.StatusText);

        Assert.False(resolver.TryRegisterSource(profilePath, out _, out _));
        App.ApplyIdentityActivationUiHealth(resolver, viewModel);
        Assert.Equal("Monitoring identity error", viewModel.StatusText);

        Directory.CreateDirectory(profilePath);
        Assert.True(resolver.TryRegisterSource(profilePath, out var owner, out var readGuard));
        using var registration = owner!;
        readGuard!.Dispose();
        registration.MarkActivated();
        App.ApplyIdentityActivationUiHealth(resolver, viewModel);

        Assert.Equal("Monitoring", viewModel.StatusText);
        Assert.True(Assert.Single(resolver.GetActivationStatusSnapshot()).IsActive);
    }

    [Fact]
    public void LostConfirmedEvidenceSuspendsActiveSourceAndDisposeRemovesItFromHealth()
    {
        using var fixture = new RegistryFixture();
        var profilePath = fixture.Path("players", "Alice");
        Directory.CreateDirectory(profilePath);
        var evidence = "volume:alice";
        var resolver = fixture.CreateResolver(_ => evidence);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        var health = new MonitoringHealthState(viewModel, action => action());
        App.WireIdentityActivationUiHealth(resolver, health, new RuntimeDiagnosticService());

        Assert.True(resolver.TryRegisterSource(profilePath, out var owner, out var readGuard));
        using var registration = owner!;
        readGuard!.Dispose();
        registration.MarkActivated();
        Assert.Equal("Monitoring", viewModel.StatusText);

        evidence = null!;
        Assert.Equal(ProfileSourceOwnershipStatus.Unavailable, registration.TryOpenRead(out var unavailableRead));
        Assert.Null(unavailableRead);

        var suspended = Assert.Single(resolver.GetActivationStatusSnapshot());
        Assert.True(suspended.IsSuspended);
        Assert.False(suspended.IsActive);
        Assert.Equal("Monitoring identity error", viewModel.StatusText);

        registration.Dispose();
        Assert.Empty(resolver.GetActivationStatusSnapshot());
        Assert.Equal("Monitoring", viewModel.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceReadGuardProtectsJunctionNamespaceAsWellAsItsTarget(bool nested)
    {
        using var fixture = new RegistryFixture();
        var target = fixture.Path("physical-players");
        var alias = fixture.Path("alias-players");
        var intermediate = fixture.Path("intermediate-players");
        Directory.CreateDirectory(System.IO.Path.Combine(target, "Alice"));
        void CreateJunction(string linkPath, string targetPath)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            Assert.True(process.WaitForExit(5000));
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        }
        if (nested)
            CreateJunction(intermediate, target);
        CreateJunction(alias, nested ? intermediate : target);
        var resolver = fixture.CreateResolver(_ => null);
        Assert.True(resolver.TryRegisterSource(System.IO.Path.Combine(alias, "Alice"), out var owner, out var read));
        using var registration = owner!;
        read!.Dispose();
        Assert.Throws<IOException>(() => Directory.Move(alias, fixture.Path("moved-alias")));
        Assert.Throws<IOException>(() => Directory.Delete(alias));
        if (nested)
            Assert.Throws<IOException>(() => Directory.Delete(intermediate));
        registration.Dispose();
        Directory.Delete(alias);
        if (nested)
            Directory.Delete(intermediate);
        Assert.True(Directory.Exists(System.IO.Path.Combine(target, "Alice")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedConfidenceSourceAliasesFailClosedWithoutCreatingASecondIdentity(bool initiallyAvailable)
    {
        using var fixture = new RegistryFixture();
        var available = initiallyAvailable;
        var path = fixture.Path("players", "Alice");
        Directory.CreateDirectory(path);
        // A relative path's alternate case is normalized before registration;
        // use an actual junction to exercise different canonical input paths.
        var alias = fixture.Path("alias");
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", alias, fixture.Path("players") })
            startInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(startInfo)!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
        }
        var resolver = fixture.CreateResolver(_ => available ? "volume:alice" : null);
        Assert.True(resolver.TryRegisterSource(path, out var owner, out var read));
        using var registration = owner!;
        read!.Dispose();
        var bytes = File.ReadAllBytes(fixture.RegistryFilePath);
        available = !initiallyAvailable;
        Assert.False(resolver.TryRegisterSource(System.IO.Path.Combine(alias, "Alice"), out var second, out var secondRead));
        Assert.Null(second);
        Assert.Null(secondRead);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistryFilePath));
        registration.Dispose();
        Directory.Delete(alias);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistedUnconfirmedIdentityCannotActivateAfterTheOwnershipLeaseEnds(bool evidenceAvailable)
    {
        using var fixture = new RegistryFixture();
        var path = fixture.Path("players", "Alice");
        Directory.CreateDirectory(path);
        var resolver = fixture.CreateResolver(_ => null);
        Assert.True(resolver.TryRegisterSource(path, out var owner, out var read));
        read!.Dispose();
        owner!.Dispose();
        var bytes = File.ReadAllBytes(fixture.RegistryFilePath);
        var restarted = fixture.CreateResolver(_ => evidenceAvailable ? "volume:alice" : null);
        Assert.False(restarted.TryRegisterSource(path, out var ambiguous, out var ambiguousRead));
        Assert.Null(ambiguous);
        Assert.Null(ambiguousRead);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistryFilePath));
    }

    [Fact]
    public void UnconfirmedSourcesRetainPhysicalNamespaceUntilTheLastRegistrationAndReadAreReleased()
    {
        using var fixture = new RegistryFixture();
        var path = fixture.Path("players", "Alice");
        Directory.CreateDirectory(path);
        var available = false;
        var resolver = fixture.CreateResolver(_ => available ? "volume:alice" : null);
        Assert.True(resolver.TryRegisterSource(path, out var first, out var firstRead));
        using var firstOwner = first!;
        firstRead!.Dispose();
        var bytes = File.ReadAllBytes(fixture.RegistryFilePath);
        available = true;
        Assert.True(resolver.TryRegisterSource(path, out var second, out var secondRead));
        using var secondOwner = second!;
        secondRead!.Dispose();
        Assert.Equal(firstOwner.Identity.Id, secondOwner.Identity.Id);
        Assert.Equal(PlayerProfileIdentityConfidence.UnconfirmedPathIdentity, secondOwner.Identity.Confidence);
        Assert.Throws<IOException>(() => Directory.Move(path, fixture.Path("retired")));
        firstOwner.Dispose();
        Assert.Throws<IOException>(() => Directory.Move(path, fixture.Path("retired")));
        Assert.Equal(ProfileSourceOwnershipStatus.Verified, secondOwner.TryOpenRead(out var read));
        using (read)
        {
            secondOwner.Dispose();
            Assert.Throws<IOException>(() => Directory.Move(path, fixture.Path("retired")));
        }
        Directory.Move(path, fixture.Path("retired"));
        Directory.CreateDirectory(path);
        // The continuous directory handle is gone. Path equality alone cannot
        // associate new physical evidence with the persisted fallback identity.
        Assert.False(resolver.TryRegisterSource(path, out var ambiguous, out var ambiguousRead));
        Assert.Null(ambiguous);
        Assert.Null(ambiguousRead);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistryFilePath));
    }

    [Fact]
    public void ConfirmedReadGuardPreventsReplacementBetweenOwnershipCheckAndSourceRead()
    {
        using var fixture = new RegistryFixture();
        var path = fixture.Path("players", "Alice");
        Directory.CreateDirectory(path);
        var resolver = fixture.CreateResolver(_ => "volume:alice");
        Assert.True(resolver.TryRegisterSource(path, out var owner, out var registrationRead));
        using var registration = owner!;
        registrationRead!.Dispose();
        Assert.Equal(ProfileSourceOwnershipStatus.Verified, registration.TryOpenRead(out var read));
        using (read)
        {
            Assert.Throws<IOException>(() => Directory.Move(path, fixture.Path("retired")));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Path("players"), fixture.Path("moved-players")));
        }
        Directory.Move(path, fixture.Path("retired"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRegistryCannotReassignAnAlreadyActiveSourceIdentity(bool confirmed)
    {
        using var fixture = new RegistryFixture();
        var path = fixture.Path("players", "Alice");
        Directory.CreateDirectory(path);
        var resolver = fixture.CreateResolver(_ => confirmed ? "volume:alice" : null);
        Assert.True(resolver.TryRegisterSource(path, out var owner, out var read));
        using var registration = owner!;
        read!.Dispose();
        File.Delete(fixture.RegistryFilePath);
        Assert.False(resolver.TryRegisterSource(path, out var reassigned, out var newRead));
        Assert.Null(reassigned);
        Assert.Null(newRead);
        Assert.False(File.Exists(fixture.RegistryFilePath));
    }

    [Theory]
    [InlineData("volume:first", "volume:second", false)]
    [InlineData("volume:first", "volume:second", true)]
    [InlineData("volume:first", null, false)]
    [InlineData(null, "volume:second", false)]
    [InlineData(null, null, false)]
    public void DuplicateIdRejectsEntireRegistryWithoutChangingBytes(
        string? firstPhysicalIdentity,
        string? secondPhysicalIdentity,
        bool uppercaseSecondId)
    {
        using var fixture = new RegistryFixture();
        var conflictingId = new PlayerProfileId(Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789"));
        var validId = new PlayerProfileId(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"));
        var firstPath = fixture.Path("first", "Alice");
        var secondPath = fixture.Path("second", "Alice");
        var validPath = fixture.Path("players", "Charlie");
        var originalBytes = fixture.WriteRegistry(
            (validId.ToString(), validPath, "volume:valid"),
            (conflictingId.ToString(), firstPath, firstPhysicalIdentity),
            (uppercaseSecondId ? conflictingId.ToString().ToUpperInvariant() : conflictingId.ToString(),
                secondPath, secondPhysicalIdentity));
        string? PhysicalIdentity(string path) => path == firstPath ? firstPhysicalIdentity
            : path == secondPath ? secondPhysicalIdentity : "volume:valid";
        var resolver = fixture.CreateResolver(PhysicalIdentity);

        // Even the unrelated, valid entry must not resolve from an invalid document.
        foreach (var path in new[] { validPath, firstPath, secondPath, fixture.Path("new", "Bob") })
        {
            Assert.False(resolver.TryResolveVerifiedProfile(path, out var resolution));
            Assert.Null(resolution);
            Assert.Equal(originalBytes, File.ReadAllBytes(fixture.RegistryFilePath));
        }

        Assert.False(fixture.CreateResolver(PhysicalIdentity).TryResolveVerifiedProfile(validPath, out var reloaded));
        Assert.Null(reloaded);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.RegistryFilePath));
        Assert.Equal([fixture.RegistryFilePath], Directory.GetFiles(fixture.DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidRegistryKeepsDistinctIdsForSeparateConfirmedOrUnconfirmedProfiles(bool unconfirmed)
    {
        using var fixture = new RegistryFixture();
        var firstId = new PlayerProfileId(Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789"));
        var secondId = new PlayerProfileId(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"));
        var firstPath = fixture.Path("first", "Alice");
        var secondPath = fixture.Path("second", "Alice");
        var originalBytes = fixture.WriteRegistry(
            (firstId.ToString(), firstPath, unconfirmed ? null : "volume:first"),
            (secondId.ToString(), secondPath, unconfirmed ? null : "volume:second"));
        string? PhysicalIdentity(string path) => unconfirmed ? null : path == firstPath ? "volume:first" : "volume:second";
        var resolver = fixture.CreateResolver(PhysicalIdentity);

        Assert.True(resolver.TryResolveVerifiedProfile(firstPath, out var first));
        Assert.True(resolver.TryResolveVerifiedProfile(secondPath, out var second));
        Assert.Equal(firstId, first!.Id);
        Assert.Equal(secondId, second!.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.All(new[] { first, second }, resolution => Assert.Equal(
            unconfirmed ? PlayerProfileIdentityConfidence.UnconfirmedPathIdentity : PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity,
            resolution.Confidence));
        Assert.True(fixture.CreateResolver(PhysicalIdentity).TryResolveVerifiedProfile(firstPath, out var reloaded));
        Assert.Equal(firstId, reloaded!.Id);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.RegistryFilePath));
    }

    [Fact]
    public void PersistedConfirmedProfileAliasesKeepOneRegistryEntryAndIdAfterRestart()
    {
        using var fixture = new RegistryFixture();
        var id = new PlayerProfileId(Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789"));
        var targetPath = fixture.Path("target", "Alice");
        var aliasPath = fixture.Path("alias", "Alice");
        fixture.WriteRegistry((id.ToString(), targetPath, "volume:shared"));
        var resolver = fixture.CreateResolver(_ => "volume:shared");

        Assert.True(resolver.TryResolveVerifiedProfile(targetPath, out var target));
        Assert.True(resolver.TryResolveVerifiedProfile(aliasPath, out var alias));
        Assert.Equal(id, target!.Id);
        Assert.Equal(id, alias!.Id);
        Assert.Equal(PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity, alias.Confidence);
        Assert.True(fixture.CreateResolver(_ => "volume:shared").TryResolveVerifiedProfile(aliasPath, out var reloaded));
        Assert.Equal(id, reloaded!.Id);
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.RegistryFilePath));
        var entry = Assert.Single(document.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Equal(id.ToString(), entry.GetProperty("Id").GetString());
        Assert.Equal(aliasPath, entry.GetProperty("CanonicalProfileDirectoryPath").GetString());
        Assert.Equal("volume:shared", entry.GetProperty("PhysicalIdentity").GetString());
    }

    [Fact]
    public void DuplicateIdIntroducedAfterSuccessfulResolutionBlocksCachedAndNewAssignments()
    {
        using var fixture = new RegistryFixture();
        var firstPath = fixture.Path("players", "Alice");
        var secondPath = fixture.Path("players", "Bob");
        var resolver = fixture.CreateResolver(path => path == firstPath ? "volume:first" : "volume:second");
        Assert.True(resolver.TryResolveVerifiedProfile(firstPath, out var original));
        var invalidBytes = fixture.WriteRegistry(
            (original!.Id.ToString(), firstPath, "volume:first"),
            (original.Id.ToString(), secondPath, "volume:second"));

        Assert.False(resolver.TryResolveVerifiedProfile(firstPath, out var cached));
        Assert.Null(cached);
        Assert.False(resolver.TryResolveVerifiedProfile(fixture.Path("players", "Charlie"), out var created));
        Assert.Null(created);
        Assert.Equal(invalidBytes, File.ReadAllBytes(fixture.RegistryFilePath));
        Assert.Equal([fixture.RegistryFilePath], Directory.GetFiles(fixture.DirectoryPath));
    }

    [Fact]
    public void DifferentPhysicalProfilesWithTheSameDisplayNameReceiveDifferentIds()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(path => path.EndsWith("first", StringComparison.OrdinalIgnoreCase) ? "volume:one" : "volume:two");

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("online", "same-name", "first"), out var first));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("unlimited", "same-name", "second"), out var second));

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.All([first, second], identity => Assert.Equal(PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity, identity!.Confidence));
    }

    [Fact]
    public void PhysicalAliasesReceiveTheSameId()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(_ => "volume:shared-profile");

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("target", "Alice"), out var target));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("junction", "Alice"), out var alias));

        Assert.Equal(target!.Id, alias!.Id);
        Assert.Equal(fixture.Path("junction", "Alice"), alias.CanonicalProfileDirectoryPath);
    }

    [Fact]
    public void ZeroWindowsFileIdFallsBackToSeparateUnconfirmedPathIdentities()
    {
        using var fixture = new RegistryFixture();
        var zeroFileId = new byte[16];
        var resolver = fixture.CreateResolver(_ => WindowsDirectoryPhysicalIdentityProvider.FormatIdentity(42, zeroFileId));

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("first", "Alice"), out var first));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("second", "Alice"), out var second));

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.All([first, second], identity => Assert.Equal(PlayerProfileIdentityConfidence.UnconfirmedPathIdentity, identity!.Confidence));
    }

    [Fact]
    public void ConfirmedIdentitySurvivesResolverRestart()
    {
        using var fixture = new RegistryFixture();
        var profile = fixture.Path("players", "Alice");

        Assert.True(fixture.CreateResolver(_ => "volume:alice").TryResolveVerifiedProfile(profile, out var first));
        Assert.True(fixture.CreateResolver(_ => "volume:alice").TryResolveVerifiedProfile(profile, out var second));

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public void ConfirmedPhysicalIdentityKeepsItsIdAfterMove()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(_ => "volume:moved-profile");

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("old", "Alice"), out var oldLocation));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("new", "Alice"), out var newLocation));

        Assert.Equal(oldLocation!.Id, newLocation!.Id);
        Assert.Equal(fixture.Path("new", "Alice"), newLocation.CanonicalProfileDirectoryPath);
    }

    [Fact]
    public void CopyWithDifferentPhysicalIdentityReceivesAnotherId()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(path => path.Contains("copy", StringComparison.OrdinalIgnoreCase) ? "volume:copy" : "volume:original");

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("original", "Alice"), out var original));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("copy", "Alice"), out var copy));

        Assert.NotEqual(original!.Id, copy!.Id);
    }

    [Fact]
    public void ReplacementAtTheSamePathDoesNotInheritTheOldConfirmedId()
    {
        using var fixture = new RegistryFixture();
        var profile = fixture.Path("players", "Alice");
        var token = "volume:before";
        var resolver = fixture.CreateResolver(_ => token);

        Assert.True(resolver.TryResolveVerifiedProfile(profile, out var original));
        token = "volume:replacement";
        Assert.True(resolver.TryResolveVerifiedProfile(profile, out var replacement));
        token = "volume:before";
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("moved", "Alice"), out var movedOriginal));

        Assert.NotEqual(original!.Id, replacement!.Id);
        Assert.Equal(original.Id, movedOriginal!.Id);
    }

    [Fact]
    public void MissingPhysicalIdentityIsPathScopedAndExplicitlyUnconfirmed()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(_ => null);

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("first", "Alice"), out var first));
        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("second", "Alice"), out var second));
        Assert.True(fixture.CreateResolver(_ => null).TryResolveVerifiedProfile(fixture.Path("first", "Alice"), out var firstAgain));

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal(first.Id, firstAgain!.Id);
        Assert.Equal(PlayerProfileIdentityConfidence.UnconfirmedPathIdentity, first.Confidence);
    }

    [Fact]
    public void NewlyConfirmedEvidenceDoesNotMergeWithEarlierUnconfirmedPathEvidence()
    {
        using var fixture = new RegistryFixture();
        var profile = fixture.Path("players", "Alice");

        Assert.True(fixture.CreateResolver(_ => null).TryResolveVerifiedProfile(profile, out var unconfirmed));
        Assert.True(fixture.CreateResolver(_ => "volume:alice").TryResolveVerifiedProfile(profile, out var confirmed));

        Assert.NotEqual(unconfirmed!.Id, confirmed!.Id);
        Assert.Equal(PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity, confirmed.Confidence);
    }

    [Fact]
    public void CorruptedExistingRegistryIsNotOverwritten()
    {
        using var fixture = new RegistryFixture();
        const string invalid = "{\"Entries\":null}";
        File.WriteAllText(fixture.RegistryFilePath, invalid);
        var resolver = fixture.CreateResolver(_ => "volume:alice");

        Assert.False(resolver.TryResolveVerifiedProfile(fixture.Path("players", "Alice"), out var resolution));

        Assert.Null(resolution);
        Assert.Equal(invalid, File.ReadAllText(fixture.RegistryFilePath));
    }

    [Fact]
    public void RegistryCorruptedAfterLoadIsNotReplacedByANewAssignment()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(path => path.EndsWith("Alice", StringComparison.OrdinalIgnoreCase) ? "volume:alice" : "volume:bob");

        Assert.True(resolver.TryResolveVerifiedProfile(fixture.Path("players", "Alice"), out _));
        const string invalid = "[]";
        File.WriteAllText(fixture.RegistryFilePath, invalid);

        Assert.False(resolver.TryResolveVerifiedProfile(fixture.Path("players", "Bob"), out var resolution));

        Assert.Null(resolution);
        Assert.Equal(invalid, File.ReadAllText(fixture.RegistryFilePath));
    }

    [Fact]
    public void FailedWriteDoesNotOverwriteEarlierAssignments()
    {
        using var fixture = new RegistryFixture();
        var resolver = fixture.CreateResolver(path => path.EndsWith("Alice", StringComparison.OrdinalIgnoreCase) ? "volume:alice" : "volume:bob");
        var alice = fixture.Path("players", "Alice");

        Assert.True(resolver.TryResolveVerifiedProfile(alice, out var original));
        using (new FileStream(fixture.RegistryFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(resolver.TryResolveVerifiedProfile(fixture.Path("players", "Bob"), out _));
        }

        Assert.True(fixture.CreateResolver(_ => "volume:alice").TryResolveVerifiedProfile(alice, out var reloaded));
        Assert.Equal(original!.Id, reloaded!.Id);
    }

    [Fact]
    public void SecondResolverReloadsTheRegistryBeforeWritingFromAnEarlierSnapshot()
    {
        using var fixture = new RegistryFixture();
        var firstResolver = fixture.CreateResolver(path => $"id:{Path.GetFileName(path)}");
        var secondResolver = fixture.CreateResolver(path => $"id:{Path.GetFileName(path)}");

        Assert.True(firstResolver.TryResolveVerifiedProfile(fixture.Path("players", "Alice"), out var alice));
        Assert.True(secondResolver.TryResolveVerifiedProfile(fixture.Path("players", "Alice"), out _));
        Assert.True(firstResolver.TryResolveVerifiedProfile(fixture.Path("players", "Bob"), out var bob));
        Assert.True(secondResolver.TryResolveVerifiedProfile(fixture.Path("players", "Carol"), out var carol));

        var reloaded = fixture.CreateResolver(path => $"id:{Path.GetFileName(path)}");
        Assert.True(reloaded.TryResolveVerifiedProfile(fixture.Path("players", "Alice"), out var savedAlice));
        Assert.True(reloaded.TryResolveVerifiedProfile(fixture.Path("players", "Bob"), out var savedBob));
        Assert.True(reloaded.TryResolveVerifiedProfile(fixture.Path("players", "Carol"), out var savedCarol));
        Assert.Equal(alice!.Id, savedAlice!.Id);
        Assert.Equal(bob!.Id, savedBob!.Id);
        Assert.Equal(carol!.Id, savedCarol!.Id);
    }

    private sealed class RegistryFixture : IDisposable
    {
        public RegistryFixture()
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string RegistryFilePath => System.IO.Path.Combine(DirectoryPath, "player-profile-identities.json");

        public string Path(params string[] parts) => System.IO.Path.Combine([DirectoryPath, .. parts]);

        public PlayerProfileIdentityResolver CreateResolver(Func<string, string?> identity) =>
            new(DirectoryPath, new DelegateDirectoryPhysicalIdentityProvider(identity));

        public byte[] WriteRegistry(params (string Id, string ProfileDirectoryPath, string? PhysicalIdentity)[] entries)
        {
            var json = JsonSerializer.Serialize(new
            {
                Entries = entries.Select(entry => new
                {
                    entry.Id,
                    CanonicalProfileDirectoryPath = entry.ProfileDirectoryPath,
                    entry.PhysicalIdentity
                })
            }, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json + "\r\n");
            File.WriteAllBytes(RegistryFilePath, bytes);
            return bytes;
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
