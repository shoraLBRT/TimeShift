using TimeShift.Web;

namespace TimeShift.Web.Tests;

public class LayeringTests
{
    [Fact]
    public void Web_adapter_references_only_application_among_timeshift_assemblies()
    {
        var references = typeof(ApiEndpoints).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("TimeShift.", StringComparison.Ordinal))
            .ToList();

        // Domain приходит транзитивно через Application (его типы видны в сигнатурах).
        Assert.Contains("TimeShift.Application", references);
        Assert.All(references, n => Assert.True(n is "TimeShift.Application" or "TimeShift.Domain", n));
    }
}
