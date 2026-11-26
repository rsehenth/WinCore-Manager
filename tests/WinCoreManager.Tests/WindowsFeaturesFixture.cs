using WinCoreManager.Services;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

internal sealed class WindowsFeaturesFixture
{
    public FakeCleanupPlatform Disk { get; } = new();
    public FakeTweakPlatform System { get; } = new();
    public FakeRestoreReader RestoreReader { get; } = new();
    public FakeWindowsTools Tools { get; } = new();
    public MemoryTweakJournal Journal { get; } = new();
    public FakeFeatureConfirmation Confirmation { get; } = new();
    public CleanupService Cleanup { get; }
    public TweakService Tweaks { get; }
    public RestorePointService Restore { get; }
    public CleanupViewModel CleanupPage { get; }
    public TweaksViewModel TweaksPage { get; }

    public WindowsFeaturesFixture(IHistoryService? history = null)
    {
        history ??= new FakeHistoryService();
        Cleanup = new(Disk, Tools, history);
        Tweaks = new(System, Journal, history, Tools);
        Restore = new(RestoreReader);
        CleanupPage = new(Cleanup, Confirmation);
        TweaksPage = new(Tweaks, Restore, Confirmation);
    }

    public sealed class FakeCleanupPlatform : ICleanupPlatform
    {
        public WindowsVolume Volume { get; set; } = new(@"D:\", "volume-fixture", 1024 * 1024 * 1024, 512 * 1024 * 1024);
        public RecycleEstimate? Estimate { get; set; } = new(2 * 1024 * 1024, 1);
        public List<string> Requested { get; } = [];
        public bool DoNotActuallyEmpty { get; set; }
        public Exception? DiskError { get; set; }
        public IReadOnlyList<CleanupCategory> Catalog { get; set; } =
        [
            new(CleanupService.RecycleBin, "Lixeira", "Arquivos recuperáveis serão removidos.", null, "Alta", "Sem elevação", false, "Registrada"),
            new("Update Cleanup", "Windows Update", "Consulte opções de recuperação.", null, "Alta", "Elevação pela ferramenta nativa", false, "Registrada"),
        ];

        public WindowsVolume ReadWindowsVolume() => DiskError is null ? Volume : throw DiskError;
        public IReadOnlyList<CleanupCategory> ReadCatalog() => Catalog;
        public RecycleEstimate? QueryRecycleBin(string root)
        {
            Assert.Equal(Volume.Root, root);
            return Estimate;
        }
        public WindowsActionResult EmptyRecycleBin(string root, nint owner)
        {
            Requested.Add(root);
            if (!DoNotActuallyEmpty) Estimate = new(0, 0);
            return new(true, "Simulado");
        }
    }

    public sealed class FakeWindowsTools : IWindowsTools
    {
        public List<(WindowsTool Tool, string? Root)> Calls { get; } = [];
        public bool Available { get; set; } = true;
        public bool AllowOpen { get; set; } = true;
        public bool IsAvailable(WindowsTool tool) => Available;
        public WindowsActionResult Open(WindowsTool tool, string? driveRoot = null)
        {
            if (!Available) return new(false, "Ferramenta indisponível.");
            Calls.Add((tool, driveRoot));
            return AllowOpen ? new(true, "Abertura simulada.") : new(false, "Elevação cancelada.");
        }
    }

    public sealed class FakeTweakPlatform : ITweakPlatform
    {
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public Guid Active { get; private set; }
        public bool Battery { get; set; } = true;
        public bool HasOtherPlan { get; set; } = true;
        public uint ProtectedHidden { get; set; } = 1;
        public Dictionary<TweakAddress, TweakValue> Values { get; } = [];
        public List<TweakAddress> Writes { get; } = [];
        public HashSet<TweakSetting> Restricted { get; } = [];
        public HashSet<TweakSetting> Unavailable { get; } = [];
        public Exception? WriteError { get; set; }
        public bool IgnoreWrite { get; set; }
        public bool FailAfterWrite { get; set; }

        public FakeTweakPlatform() => Active = A;

        public Guid ReadActivePlan() => Active;
        public bool HasBattery() => Battery;

        public TweakReading Read(TweakAddress address)
        {
            if (Unavailable.Contains(address.Setting)) throw new System.ComponentModel.Win32Exception(50);
            var restriction = Restricted.Contains(address.Setting) ? "Gerenciado por política." : null;
            var label = TweakService.Label(address.Setting);
            if (address.Setting == TweakSetting.ActivePlan)
            {
                List<TweakChoice> plans = [new(TweakValue.ForPlan(A), "Equilibrado")];
                if (HasOtherPlan) plans.Add(new(TweakValue.ForPlan(B), "Personalizado"));
                return new(address, label, TweakValue.ForPlan(Active), plans, restriction);
            }
            var value = Values.GetValueOrDefault(address) ?? new(address.IsTimeout ? 600u : 0u);
            var choices = address.IsTimeout ? new TweakChoice[] { new(new(0u), "Nunca"), new(new(600u), "10 minutos"), new(new(3600u), "1 hora") }
                : TweakService.ToggleChoices;
            return new(address, label, value, choices, restriction);
        }

        public void Write(TweakAddress address, TweakValue before, TweakValue value)
        {
            Assert.Equal(before, Read(address).Current);
            if (WriteError is not null) throw WriteError;
            Writes.Add(address);
            if (IgnoreWrite) return;
            if (address.Setting == TweakSetting.ActivePlan) Active = value.Plan!.Value;
            else Values[address] = value;
            if (FailAfterWrite) throw new IOException("Interrupção simulada.");
        }
    }

    public sealed class FakeRestoreReader : IRestorePointReader
    {
        public bool Disabled { get; set; }
        public Exception? Error { get; set; }
        public IReadOnlyList<RawRestorePoint> Points { get; set; } = [new("20261123153000.000000-180", "Ponto de teste")];
        public int Reads { get; private set; }

        public bool DisabledByPolicy() => Disabled;
        public IReadOnlyList<RawRestorePoint> Read()
        {
            Reads++;
            return Error is null ? Points : throw Error;
        }
    }

    public sealed class FakeFeatureConfirmation : IWindowsFeatureConfirmation
    {
        public bool Accepted { get; set; } = true;
        public List<(string Title, string Message)> Requests { get; } = [];
        public nint Owner => 0;
        public bool Confirm(string title, string message)
        {
            Requests.Add((title, message));
            return Accepted;
        }
    }

    public sealed class MemoryTweakJournal : ITweakJournal
    {
        private TweakJournalState _state = new();
        public Exception? Error { get; set; }
        public IDisposable Lock() => new NoOpLock();
        public TweakJournalState Read() => _state;
        public void Save(TweakJournalState state) => _state = Error is null ? state : throw Error;

        private sealed class NoOpLock : IDisposable
        {
            public void Dispose() { }
        }
    }
}

