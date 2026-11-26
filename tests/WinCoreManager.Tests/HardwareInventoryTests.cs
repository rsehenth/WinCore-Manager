using System.Text.Json;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using WinCoreManager.Localization;

namespace WinCoreManager.Tests;

public sealed class HardwareInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Professional", 1, false, ReadingState.Available, WindowsActivationState.Activated)]
    [InlineData("Professional", 2, false, ReadingState.Available, WindowsActivationState.Grace)]
    [InlineData("EnterpriseEval", 1, false, ReadingState.Available, WindowsActivationState.Grace)]
    [InlineData("Professional", 0, false, ReadingState.Available, WindowsActivationState.NotActivated)]
    [InlineData("Professional", 1, true, ReadingState.Available, WindowsActivationState.Unknown)]
    [InlineData("Professional", null, false, ReadingState.Error, WindowsActivationState.Unknown)]
    [InlineData(null, 1, false, ReadingState.Available, WindowsActivationState.Unknown)]
    public Task Audit12H04_dashboard_and_hardware_share_edition_specific_activation(string? edition, int? status,
        bool ambiguous, ReadingState query, WindowsActivationState expected) => WpfTestThread.Run(async () =>
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_OperatingSystem", R(("Caption", "Fixture Windows"), ("BuildNumber", "26100")));
        WmiRow[] rows =
        [
            R(("LicenseFamily", "Education"), ("LicenseStatus", status == 1 ? 0 : 1)),
            R(("LicenseFamily", edition), ("LicenseStatus", status))
        ];
        if (ambiguous) rows = [.. rows, R(("LicenseFamily", edition), ("LicenseStatus", 0))];
        wmi.Results["SoftwareLicensingProduct"] = new(query, rows);
        var facts = new TestWindowsFacts(edition, "24H2", 100, "UEFI", true, "x64", new(10, 0, 26100));
        var licensing = new WindowsLicensingProvider(wmi);
        var os = new WindowsOsProvider(wmi, licensing, facts);
        var dashboard = new WindowsActivationViewModel(new WindowsActivationService(os, licensing), new EssentialNavigation(_ => true));
        var hardware = new HardwareViewModel(new OsInventory(os), new HardwareTestScheduler());
        await dashboard.RefreshCommand.ExecuteAsync(null);
        await hardware.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(expected, dashboard.Snapshot?.State);
        var section = Assert.Single(hardware.Sections);
        var field = Field(Assert.Single(section.Devices), LocalizationService.Source("Message.Activation"));
        Assert.Equal(dashboard.StatusText, field.Display);
        Assert.Contains(field.Display, hardware.WindowsSummary);
    });

    private sealed class OsInventory(WindowsOsProvider os) : IHardwareInventory
    {
        public Task<IReadOnlyList<HardwareSection>> ReadAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<HardwareSection>>([os.Read(token)]);
    }

    [Theory]
    [InlineData(1, "Windows ativado", ReadingState.Available)]
    [InlineData(0, "Windows não ativado", ReadingState.Available)]
    [InlineData(2, "Em avaliação ou período de tolerância", ReadingState.Available)]
    [InlineData(5, "Windows não ativado", ReadingState.Available)]
    [InlineData(6, "Em avaliação ou período de tolerância", ReadingState.Available)]
    [InlineData(7, "Não foi possível verificar", ReadingState.NoReading)]
    [InlineData(null, "Não foi possível verificar", ReadingState.NoReading)]
    public void Ativacao_usa_status_do_produto_base_do_Windows(int? status, string display, ReadingState state)
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("SoftwareLicensingProduct", R(("LicenseFamily", "Professional"), ("LicenseStatus", status), ("PartialProductKey", "SEGREDO")));
        var result = new WindowsLicensingProvider(wmi).ReadActivation("Professional", Now, default);
        Assert.Equal(display, result.Display);
        Assert.Equal(state, result.State);
        Assert.Equal("windows", result.ComponentId);
        Assert.Equal(Now, result.Timestamp);
        var query = Assert.Single(wmi.Requests);
        Assert.Equal("LicenseFamily,LicenseStatus,EvaluationEndDate", query.Columns);
        Assert.Contains("LicenseIsAddon = FALSE", query.Filter);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(ReadingState.Restricted)]
    [InlineData(ReadingState.Error)]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.NoReading)]
    public void Wmi_indisponivel_preserva_estado_da_consulta_de_ativacao(ReadingState state)
    {
        var wmi = new TestHardwareWmi();
        wmi.Results["SoftwareLicensingProduct"] = new(state, []);
        var result = new WindowsLicensingProvider(wmi).ReadActivation("Professional", Now, default);
        Assert.Equal("Não foi possível verificar", result.Display);
        Assert.Equal(state, result.State);
    }

    [Fact]
    public void Produtos_de_licenca_conflitantes_ficam_indeterminados()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("SoftwareLicensingProduct", R(("LicenseFamily", "Professional"), ("LicenseStatus", 0)), R(("LicenseFamily", "Professional"), ("LicenseStatus", 1)));
        Assert.Equal("Não foi possível verificar", new WindowsLicensingProvider(wmi).ReadActivation("Professional", Now, default).Display);
    }

    [Theory]
    [InlineData("Microsoft Windows 11 Enterprise", "Enterprise", "25H2", "26200", 9, "x64", "UEFI", "Ativo")]
    [InlineData("Microsoft Windows 10 Pro", "Professional", "22H2", "19045", 0, "x86", "Legacy", "Não suportado")]
    [InlineData("Microsoft Windows 11 Pro", "Professional", "24H2", "26100", 12, "ARM64", "UEFI", "Desativado")]
    public void Windows_exibe_edicao_versao_build_e_arquiteturas_reportados(string name, string edition, string version,
        string build, int architecture, string arch, string firmware, string secure)
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_OperatingSystem", R(("Caption", name), ("BuildNumber", build), ("InstallDate", "20200102100000.000000+000")));
        wmi.Add("Win32_Processor", R(("Architecture", architecture)));
        var host = new TestWindowsFacts(edition, version, 99, firmware, secure == "Ativo", arch, new(10, 0, int.Parse(build)));
        var device = Assert.Single(new WindowsOsProvider(wmi, new WindowsLicensingProvider(wmi), host).Read(default).Devices);
        Assert.Equal(name, device.Name);
        Assert.Equal(edition, Field(device, "Edição").Value);
        Assert.Equal(version, Field(device, "Versão").Value);
        Assert.Equal(build + ".99", Field(device, "Compilação").Value);
        Assert.Equal(arch, Field(device, "Arquitetura do processador").Value);
        Assert.Equal(arch, Field(device, "Arquitetura do sistema").Value);
        Assert.Equal(secure, Field(device, "Secure Boot").Display);
        Assert.IsType<DateTime>(Field(device, "Data de instalação informada").Value);
    }

    [Fact]
    public void Windows_mantem_fallback_nativo_com_wmi_falhando_e_rejeita_valores_invalidos()
    {
        var wmi = new TestHardwareWmi();
        wmi.Results["Win32_OperatingSystem"] = new(ReadingState.Error, []);
        var host = new TestWindowsFacts(null, null, null, null, null, "x64", new(10, 0, 26100));
        var provider = new WindowsOsProvider(wmi, new WindowsLicensingProvider(wmi), host);
        var device = Assert.Single(provider.Read(default).Devices);
        Assert.Equal("10.0.26100", Field(device, "Versão").Value);
        Assert.Equal(ReadingState.Error, Field(device, "Nome").State);
        wmi.Add("Win32_OperatingSystem", R(("BuildNumber", "-1"), ("InstallDate", "20209999100000.000000+000")));
        wmi.Add("Win32_Processor", R(("Architecture", 90)));
        device = Assert.Single(provider.Read(default).Devices);
        Assert.Equal(ReadingState.NoReading, Field(device, "Arquitetura do processador").State);
        Assert.Null(Field(device, "Data de instalação informada").Value);
    }

    [Theory]
    [InlineData("  12th  Gen Intel(R) Core(TM) i7-12700KF  ", "12th Gen Intel(R) Core(TM) i7-12700KF")]
    [InlineData("Intel Core i7", "Modelo do processador indisponível")]
    [InlineData("Intel Processor", "Modelo do processador indisponível")]
    public void Cpu_preserva_modelo_completo_e_rejeita_rotulo_generico(string name, string expected)
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_Processor", R(("Name", name), ("NumberOfCores", 12), ("NumberOfLogicalProcessors", 20),
            ("ProcessorId", "00000000000A0671"), ("Architecture", 9)));
        var device = Assert.Single(new WindowsCpuProvider(wmi).Read(default).Devices);
        Assert.Equal(expected, device.Name);
        Assert.Equal(12UL, Field(device, "Núcleos físicos").Value);
        Assert.Equal(20UL, Field(device, "Processadores lógicos").Value);
        Assert.Equal("00000000000A0671", Field(device, "Assinatura CPUID").Value);
        Assert.Null(Field(device, "Fabricante").Value);
    }

    [Fact]
    public void Placa_mae_rejeita_smbios_generico_e_nao_projeta_serial()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_BaseBoard", R(("Manufacturer", "Default string"), ("Product", "To Be Filled By O.E.M."), ("SerialNumber", "SERIAL-INTERNO")));
        wmi.Add("Win32_BIOS", R(("SMBIOSBIOSVersion", "1.7.5"), ("ReleaseDate", "20240523100000.000000+000")));
        var result = new WindowsBoardProvider(wmi).Read(default);
        Assert.Null(Field(result.Devices[0], "Modelo").Value);
        Assert.Equal(ReadingState.Unsupported, Field(result.Devices[0], "Chipset").State);
        Assert.Equal("1.7.5", Field(result.Devices[1], "Versão do firmware").Value);
        Assert.DoesNotContain("SERIAL-INTERNO", JsonSerializer.Serialize(result));
        Assert.All(wmi.Requests, q => Assert.DoesNotContain("SerialNumber", q.Columns));
    }

    [Fact]
    public void Gpu_combina_driver_por_pci_unico_e_preserva_vram_de_64_bits()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_VideoController",
            R(("Name", "Modelo divergente"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2208&SUBSYS_39031462\IDENTIDADE-PRIVADA"), ("DriverVersion", "32.0.15")),
            R(("Name", "Outra GPU"), ("PNPDeviceID", @"PCI\VEN_1002&DEV_6658&SUBSYS_01AA1043\OUTRA-IDENTIDADE"), ("DriverVersion", "21.1")));
        var provider = new WindowsGpuProvider(wmi, new TestGraphics([
            new("NVIDIA GeForce RTX 3080 Ti", new(0x10DE, 0x2208, 0x39031462), 12UL << 30, 1, 0),
            new("Radeon de teste", new(0x1002, 0x6658, 0x01AA1043), 8UL << 30, 2, 0)
        ]), new TestGpuCapacity(new Dictionary<string, ulong> { ["gpu/dxgi/0000000000000001"] = 12UL << 30 }));
        var result = provider.Read(default);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", result.Devices[0].Name);
        Assert.Equal(12UL << 30, Field(result.Devices[0], "Memória de vídeo dedicada").Value);
        Assert.Equal("32.0.15", Field(result.Devices[0], "Driver").Value);
        Assert.Equal(12UL << 30, Field(result.Devices[0], "Memória total reportada pela NVIDIA").Value);
        Assert.DoesNotContain("IDENTIDADE-PRIVADA", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Gpus_iguais_nao_herdam_o_driver_de_linha_ambigua()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_VideoController", R(("PNPDeviceID", @"PCI\VEN_10DE&DEV_2208&SUBSYS_39031462\UM"), ("DriverVersion", "32.0.15")));
        var pci = new PciModel(0x10DE, 0x2208, 0x39031462);
        var result = new WindowsGpuProvider(wmi, new TestGraphics([new("A", pci, 8UL << 30, 1, 0), new("A", pci, 16UL << 30, 2, 0)]),
            new TestGpuCapacity(new Dictionary<string, ulong>())).Read(default);
        Assert.Equal(2, result.Devices.Select(device => device.Id).Distinct().Count());
        Assert.All(result.Devices, device => Assert.Null(Field(device, "Driver").Value));
        Assert.NotEqual(Field(result.Devices[0], "Memória de vídeo dedicada").Value, Field(result.Devices[1], "Memória de vídeo dedicada").Value);
    }

    [Fact]
    public void Gpu_sem_DXGI_nao_converte_AdapterRAM_em_vram()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_VideoController", R(("Name", "GPU do fabricante"), ("AdapterRAM", uint.MaxValue)));
        var d = Assert.Single(new WindowsGpuProvider(wmi, new TestGraphics([]), new TestGpuCapacity(new Dictionary<string, ulong>())).Read(default).Devices);
        Assert.Null(Field(d, "Memória de vídeo dedicada").Value);
        Assert.DoesNotContain("AdapterRAM", Assert.Single(wmi.Requests).Columns);
    }

    [Fact]
    public void Ram_preserva_capacidades_em_bytes_e_informa_smbios_incompleto()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_PhysicalMemory", R(("Capacity", 16UL << 30), ("SMBIOSMemoryType", 26), ("PartNumber", " KIT-001 ")),
            R(("Capacity", 16UL << 30), ("ConfiguredClockSpeed", 3200), ("SerialNumber", "RAM-PRIVADO")));
        var device = Assert.Single(new WindowsRamProvider(wmi, new TestMemory(32UL << 30, new(31UL << 30, 12, 60))).Read(default).Devices);
        Assert.Equal(32UL << 30, Field(device, "Total instalado").Value);
        Assert.Equal(31UL << 30, Field(device, "Total utilizável").Value);
        Assert.Equal(2, device.Children!.Count);
        Assert.Equal("16 GiB", Field(device.Children[0], "Capacidade").Display);
        Assert.Equal("Não informado pelo SMBIOS", Field(device.Children[1], "Modelo (Part Number)").Display);
        Assert.Equal("MT/s", Field(device.Children[1], "Velocidade configurada").Unit);
        Assert.DoesNotContain("RAM-PRIVADO", JsonSerializer.Serialize(device));
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1024UL, "1 KiB")]
    [InlineData(1073741824UL, "1 GiB")]
    [InlineData(1099511627776UL, "1 TiB")]
    public void Capacidades_sao_formatadas_sem_truncar_bytes(ulong bytes, string expected) => Assert.Equal(expected, HardwareReading.FormatBytes(bytes));

    [Fact]
    public void Discos_de_mesmo_modelo_sao_distinguidos_e_volumes_tem_espaco_proprio()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("MSFT_Disk", Disk(0, "SSD A", "private-first", 18_000), Disk(1, "SSD A", "private-second", 18_000));
        wmi.Add("MSFT_PhysicalDisk", Physical("private-second", 3, 18_000), Physical("private-first", 4, 18_000));
        wmi.Add("MSFT_Partition", R(("DiskNumber", 0), ("PartitionNumber", 1), ("DriveLetter", 'C'), ("Size", 15_000)),
            R(("DiskNumber", 1), ("PartitionNumber", 1), ("AccessPaths", new[] { @"\\?\Volume{private-guid}\" }), ("Size", 10_000)));
        wmi.Add("MSFT_Volume", R(("DriveLetter", 'C'), ("Size", 14_000), ("SizeRemaining", 9_000)),
            R(("Path", @"\\?\Volume{private-guid}\"), ("Size", 9_000), ("SizeRemaining", 3_000)));
        var result = new WindowsStorageProvider(wmi).Read(default);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("SSD SATA", Field(result.Devices[0], "Tipo").Value);
        Assert.Equal("HDD", Field(result.Devices[1], "Tipo").Value);
        var volume1 = Assert.Single(Assert.Single(result.Devices[0].Children!).Children!);
        var volume2 = Assert.Single(Assert.Single(result.Devices[1].Children!).Children!);
        Assert.Equal("Volume C:", volume1.Name);
        Assert.Equal("Volume sem letra", volume2.Name);
        Assert.Equal(3000UL, Field(volume2, "Espaço livre").Value);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Discos_conflitantes_preservam_a_fonte_de_maior_precedencia()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_DiskDrive", R(("Index", 5), ("Model", "Disco legado"), ("Size", 800), ("FirmwareRevision", "INCORRETO")));
        wmi.Add("MSFT_Disk", Disk(5, "Disco moderno", "private", 1200));
        var device = Assert.Single(new WindowsStorageProvider(wmi).Read(default).Devices);
        Assert.Equal("Disco moderno", device.Name);
        Assert.Equal(1200UL, Field(device, "Capacidade").Value);
        Assert.Null(Field(device, "Firmware").Value);
        Assert.Null(Field(device, "Fabricante").Value);
    }

    [Fact]
    public void Transporte_e_dispositivos_complementares_nao_expoem_ids_do_host()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_NetworkAdapter", R(("Name", "Adaptador Wi-Fi de teste"), ("GUID", "{0CAADDAA-8888-0000-1111-999999999999}"),
            ("MACAddress", "FF:EE:DD:CC:BB:AA")));
        wmi.Add("Win32_Battery", R(("Name", "Bateria de teste"), ("DeviceID", "PRIVADO"), ("EstimatedChargeRemaining", 255)));
        var result = new WindowsComplementaryProvider(wmi, new TestDisplays()).Read(default).ToArray();
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("0CAADDAA", json);
        Assert.DoesNotContain("FF:EE:DD", json);
        Assert.DoesNotContain("PRIVADO", json);
        Assert.Null(Field(Assert.Single(result.Single(s => s.Title == "Bateria").Devices), "Carga restante estimada").Value);
    }

    private static WmiRow Disk(int number, string model, string serial, int size) => R(("Number", number), ("Model", model),
        ("SerialNumber", serial), ("Size", size), ("BusType", 11), ("UniqueId", "shared"), ("UniqueIdFormat", 3));

    private static WmiRow Physical(string serial, int media, int size) =>
        R(("SerialNumber", serial), ("Size", size), ("MediaType", media), ("UniqueId", "shared"), ("UniqueIdFormat", 3));

    internal static HardwareReading Field(HardwareDevice device, string name) => device.Fields.Single(field => field.Label == name);
    internal static WmiRow R(params (string Name, object? Value)[] properties) => new("", properties.ToDictionary(p => p.Name, p => p.Value));
}

internal sealed class TestHardwareWmi : IHardwareWmi
{
    public Dictionary<string, WmiResult> Results { get; } = [];
    public Dictionary<string, WmiResult> Links { get; } = [];
    public List<(string Class, string Columns, string? Filter)> Requests { get; } = [];
    public void Add(string key, params WmiRow[] rows) => Results[key] = new(ReadingState.Available, rows);

    public WmiResult Read(string scope, string className, string columns, CancellationToken token, string? filter = null)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add((className, columns, filter));
        return Results.GetValueOrDefault(className) ?? new(ReadingState.NoReading, []);
    }

    public WmiResult Associated(string scope, string path, string association, string resultClass, string columns, CancellationToken token)
        => Links.GetValueOrDefault(path) ?? new(ReadingState.NoReading, []);
}

internal sealed record TestWindowsFacts(string? Edition, string? DisplayVersion, uint? BuildRevision,
    string? Firmware, bool? SecureBoot, string OsArchitecture, Version KernelVersion) : IWindowsFacts
{
    public TimeSpan Uptime => TimeSpan.FromDays(1);
}

internal sealed record TestMemory(ulong? Installed, MemorySample? Sample) : IMemoryFacts { public MemorySample? Read() => Sample; }
internal sealed record TestGraphics(IReadOnlyList<DxgiAdapter> Adapters) : IGraphicsAdapters { public IReadOnlyList<DxgiAdapter> Read(CancellationToken token) => Adapters; }
internal sealed record TestGpuCapacity(IReadOnlyDictionary<string, ulong> Values) : IGpuMemoryCapacity
{
    public IReadOnlyDictionary<string, ulong> Read(IReadOnlyList<DxgiAdapter> adapters, CancellationToken token) => Values;
}
internal sealed class TestDisplays : IDesktopDisplays { public IReadOnlyList<DesktopDisplay> Read(CancellationToken token) => []; }
