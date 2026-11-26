using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinCoreManager.Services.SelfUpdate;

namespace WinCoreManager.Tests;

public sealed class PortableRecoveryTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    [InlineData("bad-exe")]
    [InlineData("bad-managed-app")]
    [InlineData("bad-installed-updater")]
    [InlineData("missing-exe")]
    [InlineData("missing-installed-manifest")]
    [InlineData("missing-installed-updater")]
    public void Restores_only_the_authenticated_previous_package(string interrupted)
    {
        using var fixture = new Fixture();
        fixture.Interrupt(interrupted);

        fixture.Recover();
        fixture.AssertRestored();
        if (interrupted == "bad-exe")
        {
            var original = Assert.Single(Directory.GetFiles(fixture.Work, "preserved-main-*.bin"));
            Assert.Equal("corrupt executable fixture", File.ReadAllText(original));
        }
    }

    [Theory]
    [InlineData("installed-manifest")]
    [InlineData("incoming-manifest")]
    [InlineData("previous-manifest-signature")]
    [InlineData("incoming-manifest-signature")]
    [InlineData("zip-signature")]
    [InlineData("backup-hash")]
    [InlineData("incoming-hash")]
    [InlineData("partial-backup")]
    [InlineData("unknown-installed")]
    [InlineData("missing-authorization")]
    [InlineData("corrupt-authorization")]
    [InlineData("different-transaction")]
    [InlineData("already-finalized")]
    public void Refuses_uncertain_evidence_before_modifying_any_installation_file(string invalid)
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        var path = invalid switch
        {
            "installed-manifest" => Path.Combine(fixture.Root, PortablePackage.Manifest),
            "incoming-manifest" => Path.Combine(fixture.Transaction.Staging, PortablePackage.Manifest),
            "previous-manifest-signature" => Path.Combine(fixture.Transaction.Backup, PortablePackage.ManifestSignature),
            "incoming-manifest-signature" => Path.Combine(fixture.Transaction.Staging, PortablePackage.ManifestSignature),
            "zip-signature" => Path.Combine(fixture.Work, "release.zip.sig"),
            "backup-hash" or "partial-backup" => Path.Combine(fixture.Transaction.Backup, "old-only.dll"),
            "incoming-hash" => Path.Combine(fixture.Transaction.Staging, "new-only.dll"),
            "unknown-installed" => Path.Combine(fixture.Root, "fixture.dll"),
            "already-finalized" => Path.Combine(fixture.Work, PortableUpdateTransaction.CompleteFile),
            _ => Path.Combine(fixture.Work, RecoveryAuthorization.FileName),
        };
        if (invalid is "partial-backup" or "missing-authorization") File.Delete(path);
        else if (invalid is "previous-manifest-signature" or "incoming-manifest-signature" or "zip-signature")
            File.WriteAllBytes(path, new byte[64]);
        else if (invalid == "already-finalized") File.WriteAllBytes(path, []);
        else if (invalid == "different-transaction")
        {
            using var other = new Fixture();
            other.Interrupt("after");
            File.Copy(Path.Combine(other.Work, RecoveryAuthorization.FileName), path, overwrite: true);
        }
        else File.WriteAllText(path, "invalid synthetic record");

        var before = fixture.LiveFiles();
        var error = Record.Exception(fixture.Recover);
        Assert.True(error is UpdateException or IOException or InvalidDataException, error?.ToString());
        Assert.Equal(before, fixture.LiveFiles());
        Assert.True(fixture.Transaction.Pending);
    }

    [Fact]
    public void Detects_a_locked_file_and_recovers_after_it_is_released()
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        var file = Path.Combine(fixture.Root, "fixture.dll");
        var before = fixture.LiveFiles();
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(fixture.Recover);
        Assert.True(fixture.Transaction.Pending);
        Assert.Equal(before, fixture.LiveFiles());

        fixture.Recover();
        fixture.AssertRestored();
    }

    [Fact]
    public void Insufficient_space_is_refused_before_the_first_mutation()
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        var before = fixture.LiveFiles();

        var error = Assert.Throws<UpdateException>(() => fixture.Transaction.RollbackAuthenticated(fixture.PublicKey,
            requireSpace: needed =>
            {
                Assert.True(needed > 0);
                throw new UpdateException(UpdateState.InsufficientSpace);
            }));

        Assert.Equal(UpdateState.InsufficientSpace, error.State);
        Assert.True(fixture.Transaction.Pending);
        Assert.Equal(before, fixture.LiveFiles());
        fixture.Recover();
        fixture.AssertRestored();
    }

    [Fact]
    public void Rollback_can_resume_after_a_failed_swap_with_the_original_files_retained()
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        FileStream? locked = null;
        try
        {
            Assert.ThrowsAny<IOException>(() => fixture.Transaction.RollbackAuthenticated(fixture.PublicKey,
                afterReplacement: () => locked ??= File.Open(Path.Combine(fixture.Root, "fixture.dll"),
                    FileMode.Open, FileAccess.Read, FileShare.Read)));
            Assert.True(fixture.Transaction.Pending);
            Assert.NotEmpty(Directory.GetFiles(fixture.Work, "preserved-incoming-*.bin"));
        }
        finally { locked?.Dispose(); }

        fixture.Recover();
        fixture.AssertRestored();
    }

    [Fact]
    public void Repeated_interruptions_preserve_the_journal_and_converge_on_one_rollback()
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        for (var stop = 0; stop < 3; stop++)
        {
            Assert.Throws<IOException>(() => fixture.Transaction.RollbackAuthenticated(fixture.PublicKey,
                afterReplacement: () => throw new IOException("Synthetic interruption after a durable replacement.")));
            Assert.True(fixture.Transaction.Pending);
        }

        fixture.Recover();
        var first = fixture.LiveFiles();
        fixture.Recover();
        fixture.Recover();
        Assert.Equal(first, fixture.LiveFiles());
        fixture.AssertRestored();
    }

    [Fact]
    public void SQLite_settings_and_user_files_are_preserved_byte_for_byte()
    {
        using var fixture = new Fixture();
        var settings = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(settings, """{"Language":"en-US","Theme":"Light","PortableMode":true}""");
        var data = Directory.CreateDirectory(Path.Combine(fixture.Root, "Data")).FullName;
        var database = Path.Combine(data, "history.db");
        using var sqlite = new SqliteConnection($"Data Source={database};Pooling=False");
        sqlite.Open();
        using var command = sqlite.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE records(value TEXT); INSERT INTO records VALUES('fixture');";
        command.ExecuteNonQuery();
        var user = Path.Combine(fixture.Root, "user-notes.txt");
        File.WriteAllText(user, "keep this file");
        var files = new[] { settings, database, database + "-wal", database + "-shm", user };
        var before = files.Select(ReadShared).ToArray();

        fixture.Interrupt("bad-exe");
        fixture.Recover();

        Assert.Equal(before, files.Select(ReadShared).ToArray());
        command.CommandText = "SELECT value FROM records";
        Assert.Equal("fixture", command.ExecuteScalar());
    }

    [Fact]
    public void Physical_root_from_ancestral_junction_shares_the_same_nonblocking_lease()
    {
        using var fixture = new Fixture();
        var alias = fixture.CreateJunction("alias", fixture.RealParent);
        try
        {
            var throughAlias = PortableFileSystem.ResolveRoot(Path.Combine(alias, Path.GetFileName(fixture.Root)));
            Assert.Equal(fixture.Root, throughAlias, ignoreCase: true);
            using (var lease = PortableUpdateLease.TryAcquire(fixture.Root))
            {
                Assert.NotNull(lease);
                var contender = new Thread(() =>
                {
                    using var second = PortableUpdateLease.TryAcquire(throughAlias);
                    Assert.Null(second);
                });
                contender.Start();
                contender.Join();
            }
            fixture.Interrupt("after");
            new PortableUpdateTransaction(throughAlias, fixture.Work).RollbackAuthenticated(fixture.PublicKey);
            fixture.AssertRestored();
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Reparse_point_inside_package_is_refused_and_external_fixture_is_preserved()
    {
        using var fixture = new Fixture();
        fixture.Interrupt("after");
        var external = Directory.CreateDirectory(Path.Combine(fixture.Scratch, "outside")).FullName;
        var sentinel = Path.Combine(external, "empty.txt");
        File.WriteAllText(sentinel, "external disposable fixture");
        var packageFolder = Path.Combine(fixture.Root, "en-US");
        File.Delete(Path.Combine(packageFolder, "fixture.resources.dll"));
        Directory.Delete(packageFolder);
        var internalLink = fixture.CreateJunction(Path.Combine(fixture.Root, "en-US"), external);
        try
        {
            Assert.Throws<UpdateException>(fixture.Recover);
            Assert.Equal("external disposable fixture", File.ReadAllText(sentinel));
            Assert.True(fixture.Transaction.Pending);
        }
        finally { Directory.Delete(internalLink); }
    }

    private static byte[] ReadShared(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var data = new MemoryStream();
        file.CopyTo(data);
        return data.ToArray();
    }

    [Fact]
    public void Directory_handle_denies_renaming_the_directory_until_it_is_released()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "locked-dir")).FullName;
        var moved = directory + "-moved";
        using (var paths = new PortableFileSystem())
        {
            paths.PinDirectory(directory);
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, moved));
        }
        Directory.Move(directory, moved);
        Assert.True(Directory.Exists(moved));
    }

    [Fact]
    public void Lease_is_exclusive_on_the_same_thread_and_uses_the_installation_directory()
    {
        using var fixture = new Fixture();
        using (var first = PortableUpdateLease.TryAcquire(fixture.Root))
        {
            Assert.NotNull(first);
            Assert.True(File.Exists(Path.Combine(fixture.Root, ".wincore-portable.lock")));
            using var second = PortableUpdateLease.TryAcquire(fixture.Root);
            Assert.Null(second);
        }
        using var recovery = PortableUpdateLease.TryAcquire(fixture.Root);
        Assert.NotNull(recovery);
    }

    [Fact]
    public void Lease_refuses_a_redirected_lock_without_touching_its_target()
    {
        using var fixture = new Fixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Scratch, "outside-lease")).FullName;
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep outside the installation");
        var alias = fixture.CreateJunction(Path.Combine(fixture.Root, ".wincore-portable.lock"), outside);
        try
        {
            Assert.Throws<UpdateException>(() => PortableUpdateLease.TryAcquire(fixture.Root));
            Assert.Equal("keep outside the installation", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Lease_preserves_nonempty_content_at_the_reserved_path()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, ".wincore-portable.lock");
        File.WriteAllText(path, "preserve unexpected content");

        Assert.Throws<UpdateException>(() => PortableUpdateLease.TryAcquire(fixture.Root));
        Assert.Equal("preserve unexpected content", File.ReadAllText(path));
    }

    [Fact]
    public async Task Public_update_preparation_refuses_a_direct_service_call_with_no_trusted_channel()
    {
        using var client = new HttpClient();
        var unconfigured = new GitHubReleases(client, null, null, TimeSpan.Zero, TimeSpan.Zero);
        var unused = new Uri("https://example.invalid/");
        var offer = new UpdateOffer(new UpdateVersion(1, 0, 0, ""), "", unused, unused, 0, "");
        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            new PortableUpdateCoordinator(unconfigured).PrepareAsync(offer, new Progress<double>(), CancellationToken.None));
        Assert.Equal(Environment.IsPrivilegedProcess ? UpdateState.CannotWrite : UpdateState.NotConfigured, error.State);
        Assert.False(GitHubReleases.OfficialIdentityConfigured);
        Assert.Throws<UpdateException>(() => PortableUpdateCoordinator.Launch("unused", offer.Version));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string[] _previousNames =
            [PortablePackage.Executable, PortablePackage.Helper, "WinCoreManager.dll", "fixture.dll", "old-only.dll"];
        private readonly string[] _newNames =
            [PortablePackage.Executable, PortablePackage.Helper, "WinCoreManager.dll", "fixture.dll", "new-only.dll", "en-US/fixture.resources.dll"];
        private readonly Dictionary<string, string> _previousHashes;
        public string Scratch { get; } = Directory.CreateTempSubdirectory("wincore-recovery-").FullName;
        public string RealParent { get; }
        public string Root { get; }
        public string Work { get; }
        public PortableUpdateTransaction Transaction { get; }
        public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        public Fixture()
        {
            RealParent = Directory.CreateDirectory(Path.Combine(Scratch, "real")).FullName;
            Root = Directory.CreateDirectory(Path.Combine(RealParent, "portable")).FullName;
            Work = Directory.CreateDirectory(Path.Combine(Root, PortablePackage.WorkDirectory, Guid.NewGuid().ToString("N"))).FullName;
            Transaction = new PortableUpdateTransaction(Root, Work);
            WritePackage(Root, _previousNames, incoming: false);
            _previousHashes = PortablePackage.ReadManifest(Root);
            WritePackage(Transaction.Staging, _newNames, incoming: true);
            var zip = Path.Combine(Work, "release.zip");
            ZipFile.CreateFromDirectory(Transaction.Staging, zip);
            Sign(zip, zip + ".sig");
        }

        public void Interrupt(string point)
        {
            Transaction.Apply(publisherKey: PublicKey);
            if (point is "before" or "during")
            {
                foreach (var name in _previousNames.Append(PortablePackage.ManifestSignature).Append(PortablePackage.Manifest))
                {
                    if (point == "during" && name == PortablePackage.Executable) continue;
                    File.Copy(Path.Combine(Transaction.Backup, name), Path.Combine(Root, name), overwrite: true);
                }
                foreach (var name in _newNames.Except(_previousNames)) File.Delete(Path.Combine(Root, name));
            }
            if (point == "bad-exe") File.WriteAllText(Path.Combine(Root, PortablePackage.Executable), "corrupt executable fixture");
            if (point == "bad-managed-app") File.WriteAllText(Path.Combine(Root, "WinCoreManager.dll"), "corrupt assembly fixture");
            if (point == "bad-installed-updater") File.WriteAllText(Path.Combine(Root, PortablePackage.Helper), "corrupt updater fixture");
            if (point == "missing-exe") File.Delete(Path.Combine(Root, PortablePackage.Executable));
            if (point == "missing-installed-manifest") File.Delete(Path.Combine(Root, PortablePackage.Manifest));
            if (point == "missing-installed-updater") File.Delete(Path.Combine(Root, PortablePackage.Helper));
            Assert.True(Transaction.Pending);
        }

        public void Recover() => Transaction.RollbackAuthenticated(PublicKey);

        public void AssertRestored()
        {
            Assert.False(Transaction.Pending);
            Assert.True(File.Exists(Path.Combine(Work, PortableUpdateTransaction.RolledBackFile)));
            var installed = PortablePackage.ReadSignedManifest(Root, PublicKey);
            Assert.Equal(_previousHashes, installed.Files);
            PortablePackage.VerifyFiles(Root, installed.Files);
            Assert.False(File.Exists(Path.Combine(Root, "new-only.dll")));
            Assert.False(File.Exists(Path.Combine(Root, "en-US", "fixture.resources.dll")));
        }

        public string[] LiveFiles() => Directory.GetFiles(Root).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .ToArray();

        public string CreateJunction(string relative, string target)
        {
            var path = Path.Combine(Scratch, relative);
            var cmd = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{path}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var process = Process.Start(cmd)!;
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            return path;
        }

        private void WritePackage(string root, IEnumerable<string> names, bool incoming)
        {
            Directory.CreateDirectory(root);
            var lines = new List<string>();
            foreach (var name in names)
            {
                var file = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (name == "WinCoreManager.dll") File.Copy(typeof(App).Assembly.Location, file);
                else if (name is PortablePackage.Executable or PortablePackage.Helper)
                {
                    File.Copy(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe"), file);
                    if (incoming && name == PortablePackage.Executable)
                    {
                        using var content = File.Open(file, FileMode.Append);
                        content.WriteByte(123);
                    }
                }
                else File.WriteAllText(file, incoming ? "new disposable payload" : "old disposable payload");
                lines.Add($"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}  {name}");
            }
            var manifest = Path.Combine(root, PortablePackage.Manifest);
            File.WriteAllLines(manifest, lines);
            Sign(manifest, manifest + ".sig");
        }

        private void Sign(string file, string signature) =>
            File.WriteAllBytes(signature, _key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256));

        public void Dispose()
        {
            _key.Dispose();
            Directory.Delete(Scratch, recursive: true);
        }
    }
}
