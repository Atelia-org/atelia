using System;
using System.IO;
using System.Threading.Tasks;
using MemoTree.Core.Services;
using MemoTree.Core.Types;
using MemoTree.Services;
using MemoTree.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MemoTree.Tests.Views;

public class ViewManagementTests
{
    private ServiceProvider BuildServices(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddMemoTreeServices(workspaceRoot);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CanCreateViewAndUpdateDescription()
    {
        // Arrange: 创建临时工作区
        var tempDir = Path.Combine(Path.GetTempPath(), "memotree-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // 初始化工作区结构
        var sp = BuildServices(tempDir);
        var pathSvc = sp.GetRequiredService<IWorkspacePathService>();
        pathSvc.EnsureDirectoriesExist();

        var svc = sp.GetRequiredService<IMemoTreeService>();

        // Act: 创建视图，并更新描述
        var viewName = "work";
        await svc.CreateViewAsync(viewName, "Working set for coding");
        var rendered1 = await svc.RenderViewAsync(viewName);

        await svc.UpdateViewDescriptionAsync(viewName, "Focus on implementation phase");
        var rendered2 = await svc.RenderViewAsync(viewName);

        // Assert: 渲染包含描述（简单包含性断言）
        Assert.Contains("MemoTree 视图面板", rendered1);
        Assert.Contains(viewName, rendered1);
        Assert.Contains("Working set for coding", rendered1);

        Assert.Contains("MemoTree 视图面板", rendered2);
        Assert.Contains(viewName, rendered2);
        Assert.Contains("Focus on implementation phase", rendered2);
    }
}
