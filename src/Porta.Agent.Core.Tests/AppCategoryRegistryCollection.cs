namespace Porta.Agent.Core.Tests;

/// <summary>
/// xUnit collection for every test class that reads or writes AppCategoryClassifier's
/// process-wide static registries. xUnit parallelizes across collections; without this,
/// CategoryBridgeTests.ResetForTests() can wipe another class's registrations between
/// its Register call and its assertion — a flaky failure that only appears in full runs.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AppCategoryRegistryCollection
{
    public const string Name = "app-category-registry";
}
