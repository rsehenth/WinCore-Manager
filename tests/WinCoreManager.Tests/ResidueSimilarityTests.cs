using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public class ResidueSimilarityTests
{
    [Theory]
    [InlineData("Firefox", "Firefox", ResidueNameMatcher.MatchQuality.Perfect)]
    [InlineData("Firefox", "firefox", ResidueNameMatcher.MatchQuality.Perfect)]
    [InlineData("Firefox", "firefo", ResidueNameMatcher.MatchQuality.Perfect)] // distância 1
    [InlineData("Firefox", "Firefox Updated", ResidueNameMatcher.MatchQuality.Containment)]
    [InlineData("Firefox", "Firefox_Uptodate", ResidueNameMatcher.MatchQuality.Containment)] // "_" vira espaço
    public void CorrespondenciaForteReconhecida(string product, string candidate, ResidueNameMatcher.MatchQuality expected)
    {
        var (quality, similarity) = ResidueNameMatcher.MatchProduct(product, candidate);
        Assert.Equal(expected, quality);
        Assert.True(similarity >= 0);
    }

    [Fact]
    public void NomesCurtosExatosEEditorCompartilhadoSaoDistinguidos()
    {
        Assert.Equal(ResidueNameMatcher.MatchQuality.Perfect, ResidueNameMatcher.MatchProduct("GIMP", "gimp").Quality);
        Assert.Equal(ResidueNameMatcher.MatchQuality.Perfect, ResidueNameMatcher.MatchProduct("Zoom", "Zoom").Quality);
        Assert.Equal(ResidueNameMatcher.MatchQuality.None, ResidueNameMatcher.MatchProduct("Video Player", "Player").Quality);
        Assert.True(ResidueNameMatcher.MatchesPublisherName("Adobe Inc.", "Adobe"));
        Assert.False(ResidueNameMatcher.MatchesPublisherName("Adobe Inc.", "Adobe Acrobat"));
    }

    [Fact]
    public void NomesCurtosNaoGeramCorrespondencia()
    {
        // Menor nome tem 4 caracteres: o BCU rejeita para evitar falsos positivos.
        var (quality, similarity) = ResidueNameMatcher.MatchProduct("Java", "Java");
        Assert.Equal(ResidueNameMatcher.MatchQuality.None, quality);
        Assert.Equal(-1, similarity);
    }

    [Fact]
    public void NomesDiferentesNaoCorrespondem()
    {
        var (quality, _) = ResidueNameMatcher.MatchProduct("Navegador Azul", "Planilha Vermelha");
        Assert.Equal(ResidueNameMatcher.MatchQuality.None, quality);
    }

    [Fact]
    public void DistanciaFracaAindaEhReportada()
    {
        // "Aplicativo Exemplo" vs "Aplicativo Eexmplo": troca de letras, distância pequena
        // relativa ao tamanho, mas maior que 1.
        var (quality, similarity) = ResidueNameMatcher.MatchProduct("Aplicativo Exemplo", "Aplicativo Eexmplo");
        Assert.True(quality is ResidueNameMatcher.MatchQuality.Weak or ResidueNameMatcher.MatchQuality.Containment,
            $"quality={quality}, similarity={similarity}");
        Assert.True(similarity > 1);
    }

    [Theory]
    [InlineData("Exemplo 1.2.3", "Exemplo")]
    [InlineData("Exemplo (x64)", "Exemplo")]
    [InlineData("Exemplo v2", "Exemplo")]
    [InlineData("Exemplo Application", "Exemplo")]
    public void TrimRemoveVersaoESufixos(string input, string expected)
    {
        Assert.Equal(expected, ResidueNameMatcher.TrimProductName(input));
    }

    [Fact]
    public void TrimDeNuloEVazio()
    {
        Assert.Equal(string.Empty, ResidueNameMatcher.TrimProductName(null));
        Assert.Equal(string.Empty, ResidueNameMatcher.TrimProductName("   "));
    }

    [Fact]
    public void DistanciaSift4ComportamentoBasico()
    {
        Assert.Equal(0, ResidueNameMatcher.SimplestDistance("abc", "abc", 1));
        Assert.Equal(1, ResidueNameMatcher.SimplestDistance("abc", "abd", 1));
        Assert.Equal(3, ResidueNameMatcher.SimplestDistance("abc", "xyz", 1));
        Assert.Equal(3, ResidueNameMatcher.SimplestDistance("abc", "", 1));
    }

    [Fact]
    public void Sift4RealinhaUmaInsercaoSemTransformarNomeParecidoEmPropriedade()
    {
        Assert.Equal(1, ResidueNameMatcher.SimplestDistance("AcmeAlpha", "AcmeXAlpha", 1));
        Assert.NotEqual(ResidueNameMatcher.MatchQuality.Perfect, ResidueNameMatcher.MatchProduct("AcmeAlpha", "AcmeXAlpha").Quality);
        Assert.NotEqual(ResidueNameMatcher.MatchQuality.Containment, ResidueNameMatcher.MatchProduct("AcmeAlpha", "AcmeAlphabet").Quality);
        Assert.Equal(ResidueNameMatcher.MatchQuality.Containment, ResidueNameMatcher.MatchProduct("Acme Alpha", "Acme Alpha Cache").Quality);
    }
}
