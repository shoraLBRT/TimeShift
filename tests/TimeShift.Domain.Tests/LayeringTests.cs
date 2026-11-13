using TimeShift.Domain.Users;

namespace TimeShift.Domain.Tests;

public class LayeringTests
{
    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        var references = typeof(User).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n != "netstandard" && !n.StartsWith("System", StringComparison.Ordinal) && n != "mscorlib")
            .ToList();

        Assert.Empty(references);
    }
}
