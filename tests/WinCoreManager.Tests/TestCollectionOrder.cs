using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("WinCoreManager.Tests.TestCollectionOrder", "WinCoreManager.Tests")]

namespace WinCoreManager.Tests;

public sealed class TestCollectionOrder : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var order = Environment.GetEnvironmentVariable("WINCORE_TEST_COLLECTION_ORDER");
        return order switch
        {
            "forward" => testCollections.OrderBy(collection => collection.DisplayName, StringComparer.Ordinal),
            "reverse" => testCollections.OrderByDescending(collection => collection.DisplayName, StringComparer.Ordinal),
            _ => new DefaultTestCollectionOrderer().OrderTestCollections(testCollections),
        };
    }
}
