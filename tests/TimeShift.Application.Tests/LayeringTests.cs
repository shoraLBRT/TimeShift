using TimeShift.Application.Abstractions;

namespace TimeShift.Application.Tests;

public class LayeringTests
{
    [Fact]
    public void Application_depends_only_on_domain()
    {
        var references = typeof(IClock).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("TimeShift.", StringComparison.Ordinal) || n.StartsWith("Microsoft.", StringComparison.Ordinal))
            .ToList();

        Assert.All(references, n => Assert.Equal("TimeShift.Domain", n));
    }
}
