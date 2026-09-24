using System.Reflection;
using Avalonia.Headless.XUnit;

namespace LivePhotoConvert.Desktop.Tests.Harness;

public class TestConventionsTests
{
    /// <summary>
    /// Headless 用例按用例重建 Application；与其它界面用例并行时，清理阶段会跨线程访问调度器而偶发失败。
    /// </summary>
    [Fact]
    public void HeadlessTestClasses_AreInTheNonParallelCollection()
    {
        var offenders = typeof(TestConventionsTests).Assembly.GetTypes()
            .Where(type => type.GetMethods().Any(method =>
                method.IsDefined(typeof(AvaloniaFactAttribute)) || method.IsDefined(typeof(AvaloniaTheoryAttribute))))
            .Where(type => type.GetCustomAttribute<CollectionAttribute>() is not { Name: ProcessStateCollection.Name })
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(offenders);
    }
}
