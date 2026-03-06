namespace Porta.Agent.Core.Tests.Harness;

public sealed class OutputActionScenarioEnvironment : IDisposable
{
    public OutputActionScenarioEnvironment(
        DateTimeOffset startUtc,
        int seed,
        int bucketSeconds = 5,
        ScenarioDatabaseMode databaseMode = ScenarioDatabaseMode.InMemory)
    {
        Clock = new DeterministicTestClock(startUtc, bucketSeconds);
        Random = new DeterministicRandom(seed);
        Database = DeterministicScenarioDatabase.Create(databaseMode);
        NotificationController = new FakeNotificationController(Clock);
        BrightnessController = new FakeBrightnessController(Clock);
        Runner = new OutputActionScenarioRunner(Clock, NotificationController, BrightnessController);
    }

    public DeterministicTestClock Clock { get; }

    public DeterministicRandom Random { get; }

    public DeterministicScenarioDatabase Database { get; }

    public FakeNotificationController NotificationController { get; }

    public FakeBrightnessController BrightnessController { get; }

    public OutputActionScenarioRunner Runner { get; }

    public void Dispose()
    {
        Database.Dispose();
    }
}
