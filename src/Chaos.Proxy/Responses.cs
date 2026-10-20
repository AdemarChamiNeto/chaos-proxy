using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chaos.Proxy;

/// <summary>
/// Respostas de erro no mesmo formato da API de Chamados
/// (<c>{"error": {"code", "message", "details"}}</c>), que o Portal já sabe ler.
/// </summary>
public static class Responses
{
    public const string FaultHeader = "X-Chaos-Fault";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static object ErrorBody(string code, string message, object? details = null) =>
        new { error = new { code, message, details = details ?? new Dictionary<string, string>() } };

    public static IResult Error(int status, string code, string message, object? details = null) =>
        Results.Json(ErrorBody(code, message, details), Json, statusCode: status);

    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(ErrorBody(code, message), Json, ctx.RequestAborted);
    }

    /// <summary>Página parecida com a de um nginx na frente da API: o tipo de resposta que quebra quem espera JSON.</summary>
    public static Task WriteProxyHtmlAsync(HttpContext ctx, int status)
    {
        var reason = status switch { 502 => "Bad Gateway", 503 => "Service Temporarily Unavailable", 504 => "Gateway Time-out", _ => "Error" };
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html";
        return ctx.Response.WriteAsync(
            $"<html>\r\n<head><title>{status} {reason}</title></head>\r\n<body>\r\n<center><h1>{status} {reason}</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>\r\n",
            ctx.RequestAborted);
    }
}
