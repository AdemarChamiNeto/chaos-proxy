using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Chaos.Proxy;

/// <summary>
/// Limites do próprio proxy (não confundir com o 429 injetado pelo cenário "limite"): toda rota
/// tem um, por IP. Resposta 429 com Retry-After e sem o cabeçalho X-Chaos-Fault.
/// </summary>
public static class RateLimits
{
    public const string Read = "leitura";
    public const string Write = "escrita";
    public const string Proxy = "proxy";

    public static IServiceCollection AddChaosRateLimits(this IServiceCollection services, ChaosSettings settings)
    {
        services.AddRateLimiter(o =>
        {
            o.AddPolicy(Read, ctx => PerIp(ctx, 120));
            o.AddPolicy(Write, ctx => PerIp(ctx, 30));
            o.AddPolicy(Proxy, ctx => PerIp(ctx, settings.ProxyLimitPerMinute));
            o.OnRejected = async (context, token) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var r) ? (int)Math.Ceiling(r.TotalSeconds) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                await Responses.WriteErrorAsync(context.HttpContext, 429, "rate_limited", $"Muitas requisições ao proxy. Tente de novo em {retry} s.");
            };
        });
        return services;
    }

    private static RateLimitPartition<string> PerIp(HttpContext ctx, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
