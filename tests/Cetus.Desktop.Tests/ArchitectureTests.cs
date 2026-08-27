using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void RuntimeAssembly_DoesNotReferenceDesktopUiFrameworks()
    {
        string[] references = typeof(DshHost).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("System.Windows.Forms", references);
        Assert.DoesNotContain("Microsoft.Web.WebView2.Wpf", references);
        Assert.DoesNotContain("MahApps.Metro", references);
    }

    [Fact]
    public void DesktopAssembly_ReferencesTheRuntimeAssembly()
    {
        string runtimeAssemblyName = typeof(DshHost).Assembly.GetName().Name!;
        string[] references = typeof(MainWindow).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains(runtimeAssemblyName, references);
    }
}
