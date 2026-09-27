using System.Net;

namespace SkyNetwork.Site.Tests;

/// <summary>The site regulations (the project's public offer) are on the site, as a page and as the approved PDF.</summary>
public class SiteRegulationsTests
{
    [Fact]
    public async Task RegulationsAreOnTheSite_LinkedFromRegistrationAndEveryPage()
    {
        using var site = new SiteFactory();
        var c = site.Browser();
        string page = await c.HtmlAsync("/docs/terms");
        Assert.Contains("Положение об официальном интернет-сайте проекта «SkyNetwork»", page);
        foreach (var section in new[] { "1. Общие положения и терминология", "6. Обработка данных и конфиденциальность", "8. Порядок рассмотрения претензий" })
            Assert.Contains(section, page);
        Assert.Contains("SKYN-WEB-TERMS-2026/01", page);
        Assert.Contains("the Russian text is the only official one", page); // on the English site

        var pdf = await c.GetAsync("/files/skynetwork-regulations.pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);

        Assert.Contains("href=\"/docs/terms\"", await c.HtmlAsync("/register"));
        Assert.Contains("href=\"/docs/terms\"", await c.HtmlAsync("/docs"));

        await c.GetAsync("/lang/ru");
        string ru = await c.HtmlAsync("/docs/terms");
        Assert.Contains("Скачать PDF", ru);
        Assert.DoesNotContain("the Russian text is the only official one", ru);
    }
}
