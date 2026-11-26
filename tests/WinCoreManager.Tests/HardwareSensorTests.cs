using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;
using static WinCoreManager.Tests.HardwareInventoryTests;

namespace WinCoreManager.Tests;

public sealed class HardwareSensorTests
{
    [Theory]
    [InlineData(null, "%", ReadingState.NoReading)]
    [InlineData(double.NaN, "°C", ReadingState.Error)]
    [InlineData(double.PositiveInfinity, "W", ReadingState.Error)]
    [InlineData(double.NegativeInfinity, "RPM", ReadingState.Error)]
    [InlineData(-1d, "MHz", ReadingState.Error)]
    [InlineData(101d, "%", ReadingState.Error)]
    [InlineData(0d, "%", ReadingState.Available)]
    [InlineData(77d, "°C", ReadingState.Available)]
    public void Medicoes_invalidas_e_nulas_nao_viram_zero(double? value, string unit, ReadingState state)
    {
        var reading = HardwareReading.Create("sensor", "Amostra", value, "fake", default, unit);
        Assert.Equal(state, reading.State);
        Assert.Equal(state == ReadingState.Available ? value : null, reading.Value);
    }

    [Fact]
    public void Uso_da_cpu_exige_duas_amostras_e_descarta_contadores_inconsistentes()
    {
        var first = new WindowsPerformanceSensors.CpuTimes(30, 80, 20);
        Assert.Null(WindowsPerformanceSensors.CpuLoad(null, first));
        Assert.Equal(60, WindowsPerformanceSensors.CpuLoad(first, new(70, 150, 50)));
        Assert.Null(WindowsPerformanceSensors.CpuLoad(first, new(20, 80, 20)));
        Assert.Null(WindowsPerformanceSensors.CpuLoad(first, new(80, 85, 20)));
    }

    [Theory]
    [InlineData(3, ReadingState.Unsupported)]
    [InlineData(4, ReadingState.Restricted)]
    [InlineData(6, ReadingState.NoReading)]
    [InlineData(999, ReadingState.Error)]
    public void Nvml_distingue_ausencia_permissao_e_erro(int result, ReadingState state) => Assert.Equal(state, NvidiaSensors.State(result));

    [Fact]
    public void Temperatura_de_disco_valida_limites_e_descarta_indice_ambiguo()
    {
        var bytes = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 72);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), 0);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 41);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(58), 62);
        var result = WindowsDiskTemperatures.ParseTemperatures(bytes);
        Assert.Equal(ReadingState.Available, result.State);
        Assert.Equal(((ushort)0, (short)41), Assert.Single(result.Values));
        Assert.Equal(ReadingState.Error, WindowsDiskTemperatures.ParseTemperatures(bytes.AsSpan(0, 40)).State);
    }

    [Fact]
    public void Temperatura_e_saude_sao_associadas_ao_disco_mesmo_com_modelo_igual()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("MSFT_Disk", R(("Number", 0), ("Model", "Mesmo nome"), ("SerialNumber", "INTERNAL-0"), ("HealthStatus", 0)) with { Path = "disk-A" },
            R(("Number", 1), ("Model", "Mesmo nome"), ("SerialNumber", "INTERNAL-1"), ("HealthStatus", 1)) with { Path = "disk-B" });
        wmi.Links["disk-A"] = new(ReadingState.Available, [R(("Temperature", 45), ("Wear", 4))]);
        wmi.Links["disk-B"] = new(ReadingState.Restricted, []);
        using var provider = new StorageSensors(wmi, new FakeDiskTemperatures());
        var batch = provider.Read(default);
        Assert.Equal(new[] { "disk/0", "disk/1" }, batch.Groups.Select(g => g.Id));
        Assert.Equal(37d, batch.Groups[0].Readings.Single(r => r.Unit == "°C").Value);
        Assert.Equal(4d, batch.Groups[0].Readings.Single(r => r.Label == "Desgaste reportado").Value);
        Assert.DoesNotContain(batch.Groups[1].Readings, r => r.Unit == "°C");
        Assert.Contains(batch.Notices, n => n.Category.StartsWith("Disco 1", StringComparison.Ordinal) && n.State == ReadingState.Restricted);
        Assert.DoesNotContain("INTERNAL", System.Text.Json.JsonSerializer.Serialize(batch));
        Assert.Same(batch, provider.Read(default));
        Assert.Single(wmi.Requests);
    }

    [Fact]
    public async Task Agendador_nao_inicializa_ate_ser_consumido_e_libera_ao_cancelar()
    {
        var fast = new FakeProvider("Processador");
        var slow = new FakeProvider("Armazenamento", TimeSpan.FromHours(1));
        var factory = new FakeFactory(() => [fast, slow]);
        var scheduler = new SensorScheduler(factory, TimeProvider.System, TimeSpan.FromMilliseconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = scheduler.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.Equal(0, factory.Creates);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(0, slow.Reads);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, slow.Reads);
        for (var sample = 0; sample < 20; sample++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, enumerator.Current.Groups.Count);
        }
        Assert.Equal(1, slow.Reads);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(1, fast.Disposals);
        Assert.Equal(1, slow.Disposals);
    }

    [Fact]
    public async Task Agendador_serializa_sessoes_e_isola_falha_de_provedor()
    {
        var failing = new FakeProvider("Placa-mãe") { Error = new UnauthorizedAccessException() };
        var factory = new FakeFactory(() => [new FakeProvider("GPU"), failing]);
        var scheduler = new SensorScheduler(factory, TimeProvider.System, TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = scheduler.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        await using var second = scheduler.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.Equal("GPU", Assert.Single(first.Current.Groups).Name);
        Assert.Equal(ReadingState.Restricted, Assert.Single(first.Current.Notices).State);
        var pending = second.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, factory.Creates);
        await first.DisposeAsync();
        Assert.True(await pending);
        Assert.Equal(2, factory.Creates);
    }

    [Fact]
    public Task Painel_pausa_retomada_inatividade_e_fechamento_da_janela_cancelam_e_liberam() => WpfTestThread.Run(async () =>
    {
        var scheduler = new HardwareTestScheduler();
        var vm = new SensorPanelViewModel(scheduler);
        Assert.Equal(0, scheduler.Starts);
        vm.SetActive(true);
        await WaitUntil(() => vm.Groups.Count > 0);
        Assert.Equal(1, scheduler.Starts);
        vm.TogglePauseCommand.Execute(null);
        await WaitUntil(() => scheduler.Disposals == 1);
        Assert.Equal("Continuar", vm.PauseLabel);
        vm.SetActive(false);
        vm.SetActive(true);
        Assert.Equal(1, scheduler.Starts);
        vm.TogglePauseCommand.Execute(null);
        await WaitUntil(() => scheduler.Starts == 2);
        vm.SetActive(false);
        await WaitUntil(() => scheduler.Disposals == 2);

        var window = new SensorWindow(vm) { ShowActivated = false, Opacity = 0 };
        window.Show();
        vm.SetActive(true);
        await WaitUntil(() => scheduler.Starts == 3);
        window.Close();
        await vm.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, scheduler.Disposals);
        vm.SetActive(true);
        Assert.Equal(3, scheduler.Starts);
    });

    [Fact]
    public Task Falha_de_inicializacao_e_ausencia_de_sensores_nao_travam_o_painel() => WpfTestThread.Run(async () =>
    {
        var failure = new HardwareTestScheduler { Fail = true };
        var vm = new SensorPanelViewModel(failure);
        vm.SetActive(true);
        await WaitUntil(() => vm.Status.StartsWith("Falha", StringComparison.Ordinal));
        await vm.CloseAsync();
        var empty = new SensorPanelViewModel(new HardwareTestScheduler { Empty = true });
        empty.SetActive(true);
        await WaitUntil(() => empty.Status.StartsWith("Nenhuma", StringComparison.Ordinal));
        Assert.Empty(empty.Groups);
        await empty.CloseAsync();
    });

    [Fact]
    public Task Consulta_bloqueada_no_provedor_nao_bloqueia_o_dispatcher() => WpfTestThread.Run(async () =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider("CPU") { Reading = token => { entered.Set(); release.Wait(token); } };
        var scheduler = new SensorScheduler(new FakeFactory(() => [provider]), TimeProvider.System, TimeSpan.FromSeconds(1));
        var vm = new SensorPanelViewModel(scheduler);
        vm.SetActive(true);
        await WaitUntil(() => entered.IsSet);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Empty(vm.Groups);
        }
        finally { release.Set(); }
        await WaitUntil(() => vm.Groups.Count == 1);
        await vm.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, provider.Disposals);
    });

    [Fact]
    public void Modulo_importa_apenas_consultas_nativas_e_nenhum_driver_de_terceiro()
    {
        var assembly = typeof(HardwareReading).Assembly;
        var imports = assembly.GetTypes().Where(type => type.Namespace == typeof(HardwareReading).Namespace)
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Select(method => method.GetCustomAttribute<DllImportAttribute>()).OfType<DllImportAttribute>()
            .Select(import => import.EntryPoint).ToArray();
        string[] approved =
        [
            "GetFirmwareType", "GetPhysicallyInstalledSystemMemory", "GlobalMemoryStatusEx", "CreateDXGIFactory1",
            "EnumDisplayDevicesW", "EnumDisplaySettingsW", "GetSystemTimes", "GetActiveProcessorGroupCount",
            "GetActiveProcessorCount", "CallNtPowerInformation", "LoadLibraryExW", "CreateFileW", "DeviceIoControl"
        ];
        Assert.NotEmpty(imports);
        Assert.All(imports, import => Assert.Contains(import, approved));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), dependency =>
            dependency.Name is "LibreHardwareMonitorLib" or "RAMSPDToolkit" or "HidSharp");
    }

    [Theory]
    [InlineData(840, 420, 1.0)] [InlineData(1160, 760, 1.0)] [InlineData(1500, 900, 1.0)]
    [InlineData(840, 420, 1.25)] [InlineData(1160, 760, 1.25)] [InlineData(1500, 900, 1.25)]
    [InlineData(840, 420, 1.5)] [InlineData(1160, 760, 1.5)] [InlineData(1500, 900, 1.5)]
    public Task Inventario_e_painel_renderizam_em_tres_tamanhos_e_dpis(int width, int height, double scale) => WpfTestThread.Run(async () =>
    {
        var scheduler = new HardwareTestScheduler();
        var inventory = new HardwareViewModel(new HardwareTestInventory(), scheduler);
        await inventory.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(0, scheduler.Starts);
        var page = new HardwarePage { DataContext = inventory };
        Render(page, new(width - 240, height), scale);
        var button = Assert.IsType<Button>(page.FindName("SensorsButton"));
        Assert.True(button.IsEnabled && button.ActualWidth > 40 && button.Focusable);
        Assert.Equal(4, Assert.IsType<ItemsControl>(page.FindName("HardwareSummary")).Items.Count);
        var scroll = Assert.IsType<ScrollViewer>(page.FindName("HardwareScroll"));
        Assert.Equal(0, scroll.ScrollableWidth);
        var sensors = inventory.CreateSensorPanel();
        sensors.SetActive(true);
        try
        {
            await WaitUntil(() => sensors.Groups.Count > 0);
            var panel = new SensorPanel { DataContext = sensors };
            Render(panel, new(width, height), scale);
            Assert.Equal(2, Assert.IsType<ItemsControl>(panel.FindName("SensorGroups")).Items.Count);
            Assert.Equal(0, Assert.IsType<ScrollViewer>(panel.FindName("SensorScroll")).ScrollableWidth);
            Assert.True(Assert.IsType<Button>(panel.FindName("SensorPauseButton")).Focusable);
        }
        finally { await sensors.CloseAsync(); }
    });

    private static void Render(UserControl view, Size size, double scale)
    {
        VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        Assert.Equal(scale, VisualTreeHelper.GetDpi(view).DpiScaleX);
        Assert.Equal(size.Width, view.ActualWidth, 0.5);
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 200);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class FakeDiskTemperatures : IDiskTemperatures
    {
        public DiskTemperatureResult Read(uint number, string? privateSerial) => number == 0 && privateSerial == "INTERNAL-0"
            ? new(ReadingState.Available, [((ushort)0, (short)37)]) : new(ReadingState.Restricted, []);
    }

    private sealed class FakeFactory(Func<IReadOnlyList<ISensorProvider>> create) : ISensorProviderFactory
    {
        public int Creates { get; private set; }
        public IReadOnlyList<ISensorProvider> Create() { Creates++; return create(); }
    }

    private sealed class FakeProvider(string category, TimeSpan interval = default) : ISensorProvider
    {
        public string Category => category;
        public TimeSpan Interval => interval;
        public int Reads { get; private set; }
        public int Disposals { get; private set; }
        public Exception? Error { get; init; }
        public Action<CancellationToken>? Reading { get; init; }
        public SensorBatch Read(CancellationToken token)
        {
            Reads++;
            Reading?.Invoke(token);
            if (Error is not null) throw Error;
            return new([new(Category, Category, [HardwareReading.Create(Category, "Temperatura", 40d, "fake", default, "°C")])], []);
        }
        public void Dispose() => Disposals++;
    }
}

internal sealed class HardwareTestInventory : IHardwareInventory
{
    public Task<IReadOnlyList<HardwareSection>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<HardwareSection>>(
        new[] { "Sistema Operacional", "Processador", "Placa de vídeo", "Memória RAM", "Armazenamento" }
            .Select(title => new HardwareSection(title, "Modelo de teste com informação de hardware",
                [new("fixture", HardwareKind.Cpu, "Modelo de teste", [HardwareReading.Create("fixture", "Modelo", "Teste", "fake", default)])])).ToArray());
}

internal sealed class HardwareTestScheduler : ISensorScheduler
{
    public int Starts { get; private set; }
    public int Disposals { get; private set; }
    public bool Fail { get; init; }
    public bool Empty { get; init; }
    public async IAsyncEnumerable<SensorSnapshot> ObserveAsync([EnumeratorCancellation] CancellationToken token)
    {
        Starts++;
        try
        {
            await Task.Yield();
            if (Fail) throw new InvalidOperationException("Falha sintética");
            IReadOnlyList<SensorGroup> sensors = Empty ? [] :
            [
                new("cpu", "CPU de teste", [HardwareReading.Create("cpu", "Utilização total", 42d, "fake", default, "%")]),
                new("gpu", "GPU de teste", [HardwareReading.Create("gpu", "Temperatura", 50d, "fake", default, "°C")])
            ];
            yield return new(DateTimeOffset.Now, sensors, [new("Placa-mãe", ReadingState.Unsupported, "Sem sensor nesta sessão.")]);
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { Disposals++; }
    }
}
