namespace Porta.Agent.Core.Tests.Harness;

public sealed class DeterministicRandom
{
    private readonly Random _random;

    public DeterministicRandom(int seed)
    {
        Seed = seed;
        _random = new Random(seed);
    }

    public int Seed { get; }

    public double NextDouble()
    {
        return _random.NextDouble();
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        return _random.Next(minInclusive, maxExclusive);
    }

    public string SeedTag(string context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return $"seed={Seed}";
        }

        return $"{context} (seed={Seed})";
    }
}
