using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinCoreManager.Services.SelfUpdate;

namespace WinCoreManager.Tests;

public sealed class SelfUpdateTests
{
    private const string Repository = "example-test/wincore-fixture";
    private const string AssetName = "WinCoreManager-0.10.0-win-x64-portable.zip";
    private const long RepositoryId = 123;
    private static readonly UpdateVersion Installed = ParseVersion("0.9.0");
    private static readonly byte[] Asset = [10, 20, 30];
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Asset));

    [Theory]
    [InlineData("v0.10.0", "0.9.0", 1)]
    [InlineData("0.10.0+build.2", "0.10.0+build.1", 0)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-1", "1.0.0-beta", -1)]
    public void SemVer_orders_tags_numerically(string left, string right, int result) =>
        Assert.Equal(result, Math.Sign(ParseVersion(left).CompareTo(ParseVersion(right))));

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("refs/tags/v1.0.0")]
    [InlineData("1.0")]
    public void Invalid_tags_are_rejected(string version) => Assert.False(UpdateVersion.TryParse(version, out _));

    [Theory]
    [InlineData("v0.9.0", false, false, nameof(UpdateState.Current))]
    [InlineData("v0.8.1", false, false, nameof(UpdateState.Current))]
    [InlineData("v0.10.0", false, false, nameof(UpdateState.Available))]
    [InlineData("v0.10.0", true, false, nameof(UpdateState.ReleaseUnavailable))]
    [InlineData("v0.10.0", false, true, nameof(UpdateState.ReleaseUnavailable))]
    [InlineData("v0.10.0-rc.1", false, false, nameof(UpdateState.ReleaseUnavailable))]
    [InlineData("invalid", false, false, nameof(UpdateState.InvalidResponse))]
    public async Task Only_a_newer_stable_release_is_offered(string tag, bool draft, bool prerelease, string expected)
    {
        using var client = Fake(release: Release(tag, draft, prerelease));
        var result = await Releases(client).CheckAsync(Installed, CancellationToken.None);
        Assert.Equal(expected, result.State.ToString());
        if (result.State == UpdateState.Available)
        {
            Assert.Equal("0.10.0", result.Offer!.Version.ToString());
            Assert.Equal("Synthetic notes.", result.Offer.Notes);
            Assert.Equal(3, result.Offer.Size);
        }
        else
        {
            Assert.Null(result.Offer);
        }
    }

    [Fact]
    public void PacoteExigeAssinaturaDoPublicadorERejeitaAlteracao()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo());
        var signature = publisher.SignData(Asset, HashAlgorithmName.SHA256);
        ReleaseTrust.Verify(new MemoryStream(Asset), new MemoryStream(signature), publicKey);
        Assert.Throws<UpdateException>(() => ReleaseTrust.Verify(new MemoryStream(Asset), new MemoryStream(signature), null));
        Assert.Throws<UpdateException>(() => ReleaseTrust.Verify(new MemoryStream(Asset), new MemoryStream(), publicKey));
        Assert.Throws<UpdateException>(() => ReleaseTrust.Verify(new MemoryStream([1, 2, 3]), new MemoryStream(signature), publicKey));
        Assert.Throws<UpdateException>(() => ReleaseTrust.Verify(new MemoryStream(Asset), new MemoryStream(signature),
            Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.Null(ReleaseTrust.PublisherPublicKey);
    }

    [Fact]
    public async Task Disabled_or_invalid_configuration_performs_no_request()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected request.")));
        Assert.Equal(UpdateState.NotConfigured, (await Releases(client, null).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.RepositoryUnavailable, (await Releases(client, "../unknown").CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Null(GitHubReleases.OfficialRepository);
    }

    [Fact]
    public async Task Repository_identity_release_absence_and_api_limit_are_distinguished()
    {
        using var missing = Fake(repoStatus: HttpStatusCode.NotFound);
        using var identity = Fake(repoId: 456);
        using var none = Fake(releaseStatus: HttpStatusCode.NotFound);
        using var rate = Fake(releaseStatus: HttpStatusCode.Forbidden);
        using var broken = Fake(release: "{");
        Assert.Equal(UpdateState.RepositoryUnavailable, (await Releases(missing).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.RepositoryUnavailable, (await Releases(identity).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.ReleaseUnavailable, (await Releases(none).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.RateLimited, (await Releases(rate).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.InvalidResponse, (await Releases(broken).CheckAsync(Installed, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Timeout_connection_and_user_cancellation_are_distinguished()
    {
        using var dns = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Fixture DNS failure.")));
        Assert.Equal(UpdateState.Network, (await Releases(dns).CheckAsync(Installed, CancellationToken.None)).State);
        using var slow = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }));
        Assert.Equal(UpdateState.Timeout, (await Releases(slow).CheckAsync(Installed, CancellationToken.None)).State);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Releases(slow).CheckAsync(Installed, cancel.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WinCoreManager-0.10.0-win-arm64-portable.zip")]
    [InlineData("WinCoreManager-0.10.0-win-x86-portable.zip")]
    [InlineData("WinCoreManager-0.10.0-win-x64-setup.exe")]
    [InlineData("source.zip")]
    public async Task An_incompatible_or_missing_asset_is_refused(string? asset)
    {
        using var client = Fake(release: Release(asset: asset));
        var result = await Releases(client).CheckAsync(Installed, CancellationToken.None);
        Assert.Equal(UpdateState.IncompatiblePackage, result.State);
        Assert.Null(result.Offer);
        Assert.Equal($"https://github.com/{Repository}/releases/tag/v0.10.0", result.ReleasePage!.AbsoluteUri);
    }

    [Fact]
    public async Task Untrusted_asset_url_and_missing_digest_never_offer_installation()
    {
        using var moved = Fake(release: Release(download: "https://example.invalid/other.zip"));
        using var noHash = Fake(release: Release(digest: "unexpected"));
        Assert.Equal(UpdateState.IncompatiblePackage, (await Releases(moved).CheckAsync(Installed, CancellationToken.None)).State);
        Assert.Equal(UpdateState.VerificationFailed, (await Releases(noHash).CheckAsync(Installed, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task Download_validates_size_truncation_and_digest(int scenario, bool accepted)
    {
        using var temp = new Temp();
        var asset = scenario switch { 1 => Asset[..2], 2 => new byte[] { 10, 20, 99 }, _ => Asset };
        using var client = Fake(body: asset);
        var releases = Releases(client);
        var offer = (await releases.CheckAsync(Installed, CancellationToken.None)).Offer!;
        var file = Path.Combine(temp.Path, "fixture.zip");
        if (accepted)
        {
            await releases.DownloadAsync(offer, file, new Progress<double>(), CancellationToken.None);
            Assert.Equal(Asset, File.ReadAllBytes(file));
        }
        else
        {
            var error = await Assert.ThrowsAsync<UpdateException>(() =>
                releases.DownloadAsync(offer, file, new Progress<double>(), CancellationToken.None));
            Assert.Equal(UpdateState.VerificationFailed, error.State);
        }
    }

    [Fact]
    public async Task A_local_http_server_serves_synthetic_metadata_to_the_same_client()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            foreach (var body in new[] { Identity(RepositoryId), Release() })
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var data = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: {data.Length}\r\n\r\n");
                await stream.WriteAsync(header, stop.Token);
                await stream.WriteAsync(data, stop.Token);
            }
        }, stop.Token);
        try
        {
            using var transport = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var client = new HttpClient(new Handler((request, token) =>
            {
                Assert.Equal("https", request.RequestUri!.Scheme);
                Assert.Equal("api.github.com", request.RequestUri.Host);
                return transport.GetAsync($"http://127.0.0.1:{port}{request.RequestUri.AbsolutePath}", token);
            }));
            Assert.Equal(UpdateState.Available, (await Releases(client).CheckAsync(Installed, stop.Token)).State);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("../outside.dll", 0)]
    [InlineData("/absolute.dll", 0)]
    [InlineData("C:/outside.dll", 0)]
    [InlineData("en-US/../outside.dll", 0)]
    [InlineData("Data/history.db", 0)]
    [InlineData("CON.dll", 0)]
    [InlineData("WinCoreManager.dll", -1610612736)]
    public void Zip_rejects_unsafe_paths_and_links_before_creating_staging(string name, int attributes)
    {
        using var temp = new Temp();
        var zip = Path.Combine(temp.Path, "fixture.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = attributes;
            using var file = new StreamWriter(entry.Open());
            file.Write("fixture");
        }
        var stage = Path.Combine(temp.Path, "incoming");
        Assert.Throws<UpdateException>(() => PortablePackage.Extract(zip, stage, CancellationToken.None));
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public void Backup_failure_leaves_installation_untouched()
    {
        using var temp = new Temp();
        var (root, update) = SetupTransaction(temp.Path);
        File.WriteAllText(update.Backup, "cannot create backup directory");
        Assert.ThrowsAny<IOException>(() => update.Apply());
        Assert.False(update.Pending);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
    }

    [Fact]
    public async Task Synthetic_release_runs_from_HTTP_through_verified_archive_to_portable_swap()
    {
        using var temp = new Temp();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "portable")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, PortablePackage.WorkDirectory, Guid.NewGuid().ToString("N"))).FullName;
        var transaction = new PortableUpdateTransaction(root, work);
        var fixture = Path.Combine(temp.Path, "release");
        WritePackage(root, "old");
        WritePackage(fixture, "new");
        File.WriteAllText(Path.Combine(root, "settings.json"), """{"Language":"en-US","Theme":"Light"}""");
        File.WriteAllText(Path.Combine(fixture, "settings.json"), """{"Language":"pt-BR","Theme":"Dark"}""");

        var archive = Path.Combine(temp.Path, "synthetic.zip");
        ZipFile.CreateFromDirectory(fixture, archive);
        var bytes = File.ReadAllBytes(archive);
        var release = Release(assetSize: bytes.Length, digest: "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));
        using var client = Fake(release: release, body: bytes);
        var releases = Releases(client);
        var offer = (await releases.CheckAsync(Installed, CancellationToken.None)).Offer!;
        var downloaded = Path.Combine(work, "release.zip");
        await releases.DownloadAsync(offer, downloaded, new Progress<double>(), CancellationToken.None);
        PortablePackage.Extract(downloaded, transaction.Staging, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(transaction.Staging, "settings.json")));
        transaction.Apply();
        transaction.Complete();
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
        Assert.Equal("""{"Language":"en-US","Theme":"Light"}""", File.ReadAllText(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public void A_locked_obsolete_binary_rolls_back_replacements()
    {
        using var temp = new Temp();
        var (root, update) = SetupTransaction(temp.Path);
        using var lockHandle = File.Open(Path.Combine(root, "old-only.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsAny<IOException>(() => update.Apply());
        Assert.False(update.Pending);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
        Assert.False(File.Exists(Path.Combine(root, "new-only.dll")));
    }

    [Theory]
    [InlineData("Dark", "pt-BR", false)]
    [InlineData("Light", "en-US", false)]
    [InlineData("Dark", "en-US", true)]
    [InlineData("Light", "pt-BR", true)]
    public void Portable_swap_and_rollback_preserve_database_preferences_and_extra_user_files(string theme, string language, bool rollback)
    {
        using var temp = new Temp();
        var (root, update) = SetupTransaction(temp.Path);
        var settings = Path.Combine(root, "settings.json");
        var preferences = JsonSerializer.Serialize(new { Theme = theme, Language = language, PortableMode = true });
        File.WriteAllText(settings, preferences);
        var data = Directory.CreateDirectory(Path.Combine(root, "Data")).FullName;
        var database = Path.Combine(data, "history.db");
        using (var sqlite = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = "CREATE TABLE history(value TEXT); INSERT INTO history VALUES ('fixture');";
            command.ExecuteNonQuery();
        }
        var original = SHA256.HashData(File.ReadAllBytes(database));
        var userBackup = Path.Combine(root, "user-backup.json");
        File.WriteAllText(userBackup, "user owned");

        if (rollback)
        {
            Assert.Throws<IOException>(() => update.Apply(() => throw new IOException("Synthetic replacement failure.")));
            Assert.False(update.Pending);
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
            Assert.True(File.Exists(Path.Combine(root, "old-only.dll")));
            Assert.False(File.Exists(Path.Combine(root, "new-only.dll")));
        }
        else
        {
            update.Apply();
            Assert.True(update.Pending);
            update.Complete();
            Assert.False(update.Pending);
            Assert.Equal("new exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
            Assert.False(File.Exists(Path.Combine(root, "old-only.dll")));
            Assert.True(File.Exists(Path.Combine(root, "new-only.dll")));
        }

        Assert.Equal(preferences, File.ReadAllText(settings));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(database)));
        Assert.Equal("user owned", File.ReadAllText(userBackup));
    }

    [Fact]
    public void Pending_transaction_can_be_recovered_and_rollback_is_idempotent()
    {
        using var temp = new Temp();
        var (root, update) = SetupTransaction(temp.Path);
        update.Apply();
        Assert.True(update.Pending);
        Assert.Throws<UpdateException>(() => update.Apply());
        update.Rollback();
        update.Rollback();
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
        Assert.False(update.Pending);
    }

    [Fact]
    public void Recovery_revalidates_signed_manifest_after_interruption_and_rolls_back_on_failed_startup()
    {
        using var temp = new Temp();
        var (root, transaction) = SetupTransaction(temp.Path);
        var work = Path.GetDirectoryName(transaction.Staging)!;
        var archivePath = Path.Combine(work, "release.zip");
        ZipFile.CreateFromDirectory(transaction.Staging, archivePath);
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo());
        var archiveBytes = File.ReadAllBytes(archivePath);
        File.WriteAllBytes(archivePath + ".sig", publisher.SignData(archiveBytes, HashAlgorithmName.SHA256));

        FileStream? lockedBackup = null;
        try
        {
            Assert.ThrowsAny<IOException>(() => transaction.Apply(() =>
            {
                lockedBackup = File.Open(Path.Combine(transaction.Backup, "old-only.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
                throw new IOException("Synthetic interruption.");
            }));
        }
        finally { lockedBackup?.Dispose(); }
        Assert.Equal(work, Assert.Single(PortableUpdateCoordinator.FindPending(root)));
        var resumed = new PortableUpdateTransaction(root, work);
        Assert.True(resumed.InstalledManifestMatchesBackup());
        var staged = Path.Combine(transaction.Staging, "new-only.dll");
        File.WriteAllText(staged, "modified synthetic payload");
        Assert.Throws<UpdateException>(() => resumed.VerifyRecoveryEvidence(key));
        File.WriteAllText(staged, "new runtime");
        resumed.VerifyRecoveryEvidence(key);
        resumed.Rollback();
        Assert.Empty(PortableUpdateCoordinator.FindPending(root));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));

        var (secondRoot, secondTransaction) = SetupTransaction(Path.Combine(temp.Path, "probe-failure"));
        secondTransaction.Apply();
        Assert.True(secondTransaction.InstalledManifestMatchesIncoming());
        Assert.Throws<IOException>(() => secondTransaction.FinishVerifiedUpdate(() => throw new IOException("Synthetic probe failure.")));
        Assert.False(secondTransaction.Pending);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(secondRoot, PortablePackage.Executable)));
    }

    [Fact]
    public void Rollback_retains_conflicting_content_and_pending_evidence()
    {
        using var temp = new Temp();
        var (root, transaction) = SetupTransaction(temp.Path);
        transaction.Apply();
        var conflict = Path.Combine(root, PortablePackage.Executable);
        File.WriteAllText(conflict, "different local file");
        Assert.Throws<UpdateException>(transaction.Rollback);
        Assert.True(transaction.Pending);
        Assert.Equal("different local file", File.ReadAllText(conflict));
        Assert.True(File.Exists(Path.Combine(root, "new-only.dll")));
    }

    [Fact]
    public void Cleanup_prunes_only_finalized_jobs_with_managed_contents()
    {
        using var temp = new Temp();
        var (root, transaction) = SetupTransaction(temp.Path);
        transaction.Apply();
        var folder = Path.GetDirectoryName(transaction.Staging)!;
        PortableUpdateCoordinator.PruneFinalized(root);
        Assert.True(transaction.Pending);
        transaction.Complete();
        var conflict = Path.Combine(folder, "user-note.txt");
        File.WriteAllText(conflict, "preserve");
        PortableUpdateCoordinator.PruneFinalized(root);
        Assert.True(File.Exists(conflict));
        Assert.True(Directory.Exists(transaction.Backup));
        File.Delete(conflict);
        PortableUpdateCoordinator.PruneFinalized(root);
        Assert.False(Directory.Exists(folder));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(root, PortablePackage.Executable)));
    }

    [Fact]
    public void Lock_serializes_update_against_running_app_and_other_updates()
    {
        using var temp = new Temp();
        using var application = PortableUpdateLease.TryAcquire(temp.Path);
        Assert.NotNull(application);
        PortableUpdateLease? other = null;
        var thread = new Thread(() => other = PortableUpdateLease.TryAcquire(temp.Path));
        thread.Start();
        thread.Join();
        Assert.Null(other);
        Assert.Equal(UpdateState.InsufficientSpace,
            Assert.Throws<UpdateException>(() => PortablePackage.RequireSpace(temp.Path, long.MaxValue)).State);
        Assert.Equal(UpdateState.CannotWrite,
            Assert.Throws<UpdateException>(() => PortablePackage.ResolveRoot(Path.GetPathRoot(temp.Path)!)).State);
    }

    private static (string Root, PortableUpdateTransaction Update) SetupTransaction(string temp)
    {
        var root = Directory.CreateDirectory(Path.Combine(temp, "portable")).FullName;
        var work = Path.Combine(root, PortablePackage.WorkDirectory, Guid.NewGuid().ToString("N"));
        var update = new PortableUpdateTransaction(root, work);
        WritePackage(root, "old");
        WritePackage(update.Staging, "new");
        return (root, update);
    }

    private static void WritePackage(string directory, string value)
    {
        Directory.CreateDirectory(directory);
        var contents = new Dictionary<string, string>
        {
            [PortablePackage.Executable] = value + " exe",
            [PortablePackage.Helper] = value + " helper",
            [value + "-only.dll"] = value + " runtime",
        };
        foreach (var (name, text) in contents)
        {
            File.WriteAllText(Path.Combine(directory, name), text);
        }
        File.WriteAllLines(Path.Combine(directory, PortablePackage.Manifest), contents.Keys.Select(name =>
            $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name))))}  {name}"));
    }

    private static UpdateVersion ParseVersion(string version) =>
        UpdateVersion.TryParse(version, out var parsed) ? parsed : throw new ArgumentException(version);

    private static GitHubReleases Releases(HttpClient client, string? repository = Repository) =>
        new(client, repository, RepositoryId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private static string Release(string tag = "v0.10.0", bool draft = false, bool prerelease = false,
        string? asset = AssetName, string? download = null, string? digest = null, int? assetSize = null) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease, body = "Synthetic notes.",
            assets = asset is null ? [] : new[]
            {
                new
                {
                    name = asset, state = "uploaded", content_type = "application/zip", size = assetSize ?? Asset.Length,
                    browser_download_url = download ?? $"https://github.com/{Repository}/releases/download/{tag}/{asset}",
                    digest = digest ?? "sha256:" + Digest,
                }
            },
        });

    private static string Identity(long id) => JsonSerializer.Serialize(new { full_name = Repository, id, @private = false });

    private static HttpClient Fake(HttpStatusCode repoStatus = HttpStatusCode.OK, long repoId = RepositoryId,
        HttpStatusCode releaseStatus = HttpStatusCode.OK, string? release = null, byte[]? body = null) =>
        new(new Handler((request, _) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            var response = request.RequestUri.AbsolutePath switch
            {
                $"/repos/{Repository}" => new HttpResponseMessage(repoStatus) { Content = new StringContent(Identity(repoId)) },
                $"/repos/{Repository}/releases/latest" => new HttpResponseMessage(releaseStatus) { Content = new StringContent(release ?? Release()) },
                $"/{Repository}/releases/download/v0.10.0/{AssetName}" when body is not null =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) },
                _ => throw new InvalidOperationException(request.RequestUri.AbsoluteUri),
            };
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
            }
            return Task.FromResult(response);
        }));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class Temp : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(new TestSettings().DataDirectory).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
