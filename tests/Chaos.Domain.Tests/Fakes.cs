using Chaos.Domain;

namespace Chaos.Domain.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Devolve os valores na ordem dada (e repete o último).</summary>
internal sealed class ScriptedRandom(params double[] values) : IRandomSource
{
    private int _i;
    public int LastMin { get; private set; }
    public int LastMax { get; private set; }

    public double NextDouble() => values[Math.Min(_i++, values.Length - 1)];

    public int Next(int min, int max)
    {
        LastMin = min;
        LastMax = max;
        return min + (int)Math.Round((max - min) * NextDouble());
    }
}
