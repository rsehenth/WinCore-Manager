using WinCoreManager.Services;
using Xunit;

namespace WinCoreManager.Tests;

public sealed class EssentialDetectionTests
{
    [Fact]
    public void SeparatesDesktopRuntimeFromCoreAndKeepsArchitecture()
    {
        var result = EssentialInstallationService.Evaluate(new EssentialInventory(
            [], true, true, [new("Microsoft.NETCore.App", "10.0.8", "x64"), new("Microsoft.WindowsDesktop.App", "8.0.14", "x86")],
            true, [], EssentialState.NotInstalled, []));
        Assert.Equal(EssentialState.NotInstalled, result["desktop10"].State);
        Assert.Equal(new EssentialDetection(EssentialState.Installed, "8.0.14 (x86)"), result["desktop8"]);
        Assert.Equal(EssentialState.NotVerified, result["directx"].State);
    }

    [Fact]
    public void PositiveEvidenceSurvivesPartialInventoryWithoutFalseAbsence()
    {
        var result = EssentialInstallationService.Evaluate(new EssentialInventory(
            [new("WinRAR 7.0 (64-bit)", "7.0", "win.rar GmbH")], false, false, [], false, [], EssentialState.NotVerified, []));
        Assert.Equal(new EssentialDetection(EssentialState.Installed, "7.0"), result["winrar"]);
        Assert.Equal(EssentialState.NotVerified, result["vscode"].State);
        Assert.Equal(EssentialState.NotVerified, result["framework35"].State);
    }

    [Fact]
    public void BundleRequiresLegacyComponentsForEachArchitecture()
    {
        VcRuntime[] modern = [new("v14", "x64", "14.44.1"), new("v14", "x86", "14.44.1")];
        var inventory = new EssentialInventory([], true, true, [], true, modern, EssentialState.NotInstalled, []);
        var result = EssentialInstallationService.Evaluate(inventory);
        Assert.Equal(EssentialState.Installed, result["vc-redist"].State);
        Assert.Equal(EssentialState.NotInstalled, result["vc-aio"].State);
        Assert.Equal(EssentialState.NotVerified, result["orca"].State);
    }
}
