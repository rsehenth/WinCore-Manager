using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using WinCoreManager.Models;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class UninstallerDateTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void ApresentacaoMantemDiaMesAnoMesmoEmOutraCultura(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.Equal("23/11/2026", Row("Programa", new DateTime(2026, 11, 23)).InstallDateText);
            Assert.Equal("—", Row("Programa sem data", null).InstallDateText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(ListSortDirection.Ascending)]
    [InlineData(ListSortDirection.Descending)]
    public void OrdenacaoWpfComparaDatasEMantemEmpatesDeterministicosENulasNoFim(ListSortDirection direction)
    {
        var outubroA = Row("A", new DateTime(2026, 10, 28), "A");
        var outubroB1 = Row("B", new DateTime(2026, 10, 28), "B1");
        var outubroB2 = Row("B", new DateTime(2026, 10, 28), "B2");
        var agosto = Row("Z", new DateTime(2026, 8, 30));
        var dezembroAnterior = Row("Y", new DateTime(2025, 12, 31));
        var semData = Row("Ausente", null);
        var view = new ListCollectionView(new[] { semData, outubroB2, agosto, outubroB1, dezembroAnterior, outubroA });

        using (view.DeferRefresh())
        {
            foreach (var sort in UninstallersPage.GetCatalogSortDescriptions([new(nameof(UninstallerItemRow.InstallDate), direction)]))
            {
                view.SortDescriptions.Add(sort);
            }
        }

        UninstallerItemRow[] expected = direction == ListSortDirection.Ascending
            ? [dezembroAnterior, agosto, outubroA, outubroB1, outubroB2, semData]
            : [outubroA, outubroB1, outubroB2, agosto, dezembroAnterior, semData];
        Assert.Equal(expected, view.Cast<UninstallerItemRow>());
    }

    private static UninstallerItemRow Row(string name, DateTime? date, string registryPath = "") =>
        new(new InstalledProgram { Name = name, InstallDate = date, RegistryPath = registryPath });
}
