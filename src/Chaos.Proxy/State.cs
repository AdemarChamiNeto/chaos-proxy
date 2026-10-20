using System.Security.Cryptography;
using Chaos.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace Chaos.Proxy;

/// <summary>Regras globais (valem para todo mundo, ex.: o Portal). Troca atômica da referência.</summary>
public sealed class RuleStore
{
    private RuleSet _current = RuleSet.Empty;

    public RuleSet Current => Volatile.Read(ref _current);

    public void Replace(RuleSet rules) => Volatile.Write(ref _current, rules);
}

/// <summary>
/// Cenário escolhido por cada visitante da demo, guardado pelo cookie da sessão. Assim um
/// visitante não muda o que o outro vê. Cache com tamanho máximo e expiração: não cresce para sempre.
/// </summary>
public sealed class SessionStore : IDisposable
{
    public const string Cookie = "chaos_sessao";
    public const int MaxSessions = 10_000;
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxSessions });

    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Set(string id, Scenario scenario) =>
        _cache.Set(id, scenario, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = Idle });

    public Scenario? Get(string? id) => id is not null && _cache.TryGetValue(id, out Scenario? s) ? s : null;

    public void Remove(string id) => _cache.Remove(id);

    public void Dispose() => _cache.Dispose();
}

/// <summary>Uma linha do log: sem query string e sem cabeçalhos (lá podem ir tokens).</summary>
public sealed record LogEntry(DateTimeOffset At, string Method, string Path, int Status, long Ms, string? Fault, string? Session);

/// <summary>As últimas <see cref="Capacity"/> requisições, em memória.</summary>
public sealed class RequestLog
{
    public const int Capacity = 200;

    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Lock _lock = new();

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.AddFirst(entry);
            if (_entries.Count > Capacity) _entries.RemoveLast();
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(Func<LogEntry, bool>? filter = null)
    {
        lock (_lock) return (filter is null ? _entries : _entries.Where(filter)).ToList();
    }
}
