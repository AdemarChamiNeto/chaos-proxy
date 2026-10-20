using Chaos.Domain;

namespace Chaos.Domain.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData("/**", "/", true)]
    [InlineData("/**", "/api/tickets/7/comments", true)]
    [InlineData("/api/tickets", "/api/tickets", true)]
    [InlineData("/api/tickets", "/api/tickets/", true)]
    [InlineData("/api/tickets", "/API/Tickets", true)]
    [InlineData("/api/tickets", "/api/tickets/7", false)]
    [InlineData("/api/tickets/*", "/api/tickets/7", true)]
    [InlineData("/api/tickets/*", "/api/tickets", false)]
    [InlineData("/api/tickets/*", "/api/tickets/7/comments", false)]
    [InlineData("/api/**", "/api", true)]
    [InlineData("/api/**", "/apis/x", false)]
    [InlineData("/api/*/comments", "/api/tickets/comments", true)]
    [InlineData("/**/comments", "/api/tickets/7/comments", true)]
    [InlineData("/**/comments", "/api/tickets/7", false)]
    [InlineData("/api/tickets", "/api/tickets?page=2", true)]
    public void Casa_segmentos_curinga_e_resto(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, PathPattern.Parse(pattern).Matches(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("api/tickets")]
    [InlineData("/api/tick*")]
    [InlineData("/a**b")]
    [InlineData("/1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17/18/19/20/21")]
    public void Recusa_padrao_invalido(string pattern)
    {
        Assert.False(PathPattern.TryParse(pattern, out _));
    }

    [Fact]
    public void Muitos_curingas_nao_travam()
    {
        // pior caso de backtracking: 9 "**" (19 segmentos, perto do limite de 20) e um caminho longo que não casa
        var p = PathPattern.Parse(string.Concat(Enumerable.Repeat("/**/a", 9)) + "/fim");
        var path = string.Concat(Enumerable.Repeat("/a", 200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(p.Matches(path));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"levou {sw.ElapsedMilliseconds} ms");
    }
}
