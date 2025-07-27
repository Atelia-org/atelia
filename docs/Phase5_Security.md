# MemoTree 权限管理和安全策略 (Phase 5)

> **版本**: v1.0  
> **创建日期**: 2025-07-25  
> **基于**: Core_Types_Design.md 第13节  
> **依赖**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md)  
> **状态**: 🚧 开发中  

## 概述

本文档定义了MemoTree系统的权限管理、安全策略和审计日志体系。作为Phase 5企业特性的核心组件，这些安全机制为系统提供了基于角色的访问控制（RBAC）、细粒度权限检查、安全上下文管理和完整的审计追踪能力。

## 实施优先级

1. **立即实现**: Permission枚举、ResourceType、PermissionContext、SecurityContext
2. **第一周**: IPermissionChecker、ISecurityContextProvider接口
3. **第二周**: 审计日志系统（AuditEvent、IAuditLogService）
4. **第三周**: 高级安全特性和集成测试

## 1. 权限模型

### 1.1 权限类型定义

```csharp
/// <summary>
/// 权限类型
/// 使用Flags特性支持权限组合
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>
    /// 无权限
    /// </summary>
    None = 0,
    
    /// <summary>
    /// 读取权限
    /// </summary>
    Read = 1,
    
    /// <summary>
    /// 写入权限
    /// </summary>
    Write = 2,
    
    /// <summary>
    /// 删除权限
    /// </summary>
    Delete = 4,
    
    /// <summary>
    /// 执行权限
    /// </summary>
    Execute = 8,
    
    /// <summary>
    /// 管理员权限
    /// </summary>
    Admin = 16,
    
    /// <summary>
    /// 所有权限
    /// </summary>
    All = Read | Write | Delete | Execute | Admin
}

/// <summary>
/// 资源类型
/// 定义系统中可以被权限控制的资源类型
/// </summary>
public enum ResourceType
{
    /// <summary>
    /// 认知节点
    /// </summary>
    Node,
    
    /// <summary>
    /// 视图
    /// </summary>
    View,
    
    /// <summary>
    /// 工作空间
    /// </summary>
    Workspace,
    
    /// <summary>
    /// 系统级资源
    /// </summary>
    System
}
```

### 1.2 权限上下文

```csharp
/// <summary>
/// 权限上下文
/// 包含权限检查所需的所有信息
/// </summary>
public record PermissionContext
{
    /// <summary>
    /// 用户ID
    /// </summary>
    public string UserId { get; init; } = string.Empty;
    
    /// <summary>
    /// 用户角色列表
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    
    /// <summary>
    /// 资源类型
    /// </summary>
    public ResourceType ResourceType { get; init; }
    
    /// <summary>
    /// 资源ID
    /// </summary>
    public string ResourceId { get; init; } = string.Empty;
    
    /// <summary>
    /// 请求的权限
    /// </summary>
    public Permission RequestedPermission { get; init; }
    
    /// <summary>
    /// 附加上下文信息
    /// </summary>
    public IReadOnlyDictionary<string, object> AdditionalContext { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>
/// 权限检查结果
/// </summary>
public record PermissionResult
{
    /// <summary>
    /// 是否授予权限
    /// </summary>
    public bool IsGranted { get; init; }
    
    /// <summary>
    /// 权限检查结果说明
    /// </summary>
    public string Reason { get; init; } = string.Empty;
    
    /// <summary>
    /// 所需角色列表
    /// </summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = Array.Empty<string>();
    
    /// <summary>
    /// 已授予的权限列表
    /// </summary>
    public IReadOnlyList<Permission> GrantedPermissions { get; init; } = Array.Empty<Permission>();
}
```

## 2. 权限检查服务

### 2.1 权限检查器接口

```csharp
/// <summary>
/// 权限检查器接口
/// 提供权限验证的核心功能
/// </summary>
public interface IPermissionChecker
{
    /// <summary>
    /// 检查权限
    /// </summary>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限检查结果</returns>
    Task<PermissionResult> CheckPermissionAsync(PermissionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量检查权限
    /// </summary>
    /// <param name="contexts">权限上下文列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限检查结果字典</returns>
    Task<IReadOnlyDictionary<string, PermissionResult>> CheckPermissionsBatchAsync(IEnumerable<PermissionContext> contexts, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取用户对资源的所有权限
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="resourceType">资源类型</param>
    /// <param name="resourceId">资源ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限列表</returns>
    Task<IReadOnlyList<Permission>> GetUserPermissionsAsync(string userId, ResourceType resourceType, string resourceId, CancellationToken cancellationToken = default);
}
```

## 3. 安全上下文管理

### 3.1 安全上下文

```csharp
/// <summary>
/// 安全上下文
/// 包含当前用户的安全信息
/// </summary>
public record SecurityContext
{
    /// <summary>
    /// 用户ID
    /// </summary>
    public string UserId { get; init; } = string.Empty;
    
    /// <summary>
    /// 用户名
    /// </summary>
    public string UserName { get; init; } = string.Empty;
    
    /// <summary>
    /// 用户角色列表
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    
    /// <summary>
    /// 认证时间
    /// </summary>
    public DateTime AuthenticatedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// 会话ID
    /// </summary>
    public string SessionId { get; init; } = string.Empty;
    
    /// <summary>
    /// IP地址
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;
    
    /// <summary>
    /// 用户声明信息
    ///
    /// 类型约定与 NodeMetadata.CustomProperties 相同：
    /// - 支持基本类型：string, int, long, double, bool, DateTime
    /// - 支持集合类型：string[], List&lt;string&gt;
    /// - 使用 CustomPropertiesExtensions 提供的安全访问方法
    /// </summary>
    public IReadOnlyDictionary<string, object> Claims { get; init; } =
        new Dictionary<string, object>();
}
```

### 3.2 安全上下文提供器

```csharp
/// <summary>
/// 安全上下文提供器接口
/// 管理当前用户的安全上下文
/// </summary>
public interface ISecurityContextProvider
{
    /// <summary>
    /// 获取当前安全上下文
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>安全上下文，如果未认证则返回null</returns>
    Task<SecurityContext?> GetCurrentContextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置安全上下文
    /// </summary>
    /// <param name="context">安全上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SetContextAsync(SecurityContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除安全上下文
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task ClearContextAsync(CancellationToken cancellationToken = default);
}
```

## 4. 审计日志系统

### 4.1 审计事件类型

```csharp
/// <summary>
/// 审计事件类型
/// 定义系统中需要审计的操作类型
/// </summary>
public enum AuditEventType
{
    // 节点操作
    NodeCreated,
    NodeUpdated,
    NodeDeleted,
    NodeMoved,
    
    // 关系操作
    RelationAdded,
    RelationRemoved,
    
    // 视图操作
    ViewChanged,
    
    // 权限操作
    PermissionGranted,
    PermissionRevoked,
    
    // 用户操作
    UserLogin,
    UserLogout,
    
    // 系统操作
    SystemStarted,
    SystemStopped,
    ConfigurationChanged
}
```

### 4.2 审计事件

```csharp
/// <summary>
/// 审计事件
/// 记录系统中的操作和变更
/// </summary>
public record AuditEvent
{
    /// <summary>
    /// 事件唯一标识符
    /// </summary>
    public string Id { get; init; } = GuidEncoder.ToIdString(Guid.NewGuid());

    /// <summary>
    /// 事件类型
    /// </summary>
    public AuditEventType EventType { get; init; }

    /// <summary>
    /// 事件时间戳
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 用户ID
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// 用户名
    /// </summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// 资源类型
    /// </summary>
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>
    /// 资源ID
    /// </summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>
    /// 执行的操作
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// 事件描述
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 详细信息
    ///
    /// 类型约定与 NodeMetadata.CustomProperties 相同：
    /// - 支持基本类型：string, int, long, double, bool, DateTime
    /// - 支持集合类型：string[], List&lt;string&gt;
    /// - 使用 CustomPropertiesExtensions 提供的安全访问方法
    /// </summary>
    public IReadOnlyDictionary<string, object> Details { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// IP地址
    /// </summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>
    /// 用户代理
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>
    /// 操作是否成功
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// 错误消息（如果操作失败）
    /// </summary>
    public string? ErrorMessage { get; init; }
}
```

### 4.3 审计查询条件

```csharp
/// <summary>
/// 审计查询条件
/// 用于查询和过滤审计日志
/// </summary>
public record AuditQuery
{
    /// <summary>
    /// 开始时间
    /// </summary>
    public DateTime? StartTime { get; init; }

    /// <summary>
    /// 结束时间
    /// </summary>
    public DateTime? EndTime { get; init; }

    /// <summary>
    /// 事件类型过滤
    /// </summary>
    public IReadOnlyList<AuditEventType>? EventTypes { get; init; }

    /// <summary>
    /// 用户ID过滤
    /// </summary>
    public IReadOnlyList<string>? UserIds { get; init; }

    /// <summary>
    /// 资源类型过滤
    /// </summary>
    public IReadOnlyList<string>? ResourceTypes { get; init; }

    /// <summary>
    /// 资源ID过滤
    /// </summary>
    public IReadOnlyList<string>? ResourceIds { get; init; }

    /// <summary>
    /// 成功状态过滤
    /// </summary>
    public bool? Success { get; init; }

    /// <summary>
    /// 跳过记录数
    /// </summary>
    public int Skip { get; init; } = 0;

    /// <summary>
    /// 获取记录数
    /// </summary>
    public int Take { get; init; } = 100;

    /// <summary>
    /// 搜索文本
    /// </summary>
    public string? SearchText { get; init; }
}
```

## 5. 审计日志服务

### 5.1 审计日志服务接口

```csharp
/// <summary>
/// 审计日志服务接口
/// 提供审计日志的记录、查询和管理功能
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// 记录审计事件
    /// </summary>
    /// <param name="auditEvent">审计事件</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task LogEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量记录审计事件
    /// </summary>
    /// <param name="auditEvents">审计事件列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task LogEventsBatchAsync(IEnumerable<AuditEvent> auditEvents, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询审计日志
    /// </summary>
    /// <param name="query">查询条件</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>审计事件列表</returns>
    Task<IReadOnlyList<AuditEvent>> QueryEventsAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步枚举审计日志
    /// </summary>
    /// <param name="query">查询条件</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>审计事件异步枚举</returns>
    IAsyncEnumerable<AuditEvent> QueryEventsStreamAsync(AuditQuery query, CancellationToken cancellationToken = default);
}
```

## 6. 安全配置选项

### 6.1 安全配置

```csharp
/// <summary>
/// 安全配置选项
/// </summary>
public class SecurityOptions
{
    /// <summary>
    /// 是否启用权限检查
    /// </summary>
    public bool EnablePermissionCheck { get; set; } = true;

    /// <summary>
    /// 是否启用审计日志
    /// </summary>
    public bool EnableAuditLog { get; set; } = true;

    /// <summary>
    /// 会话超时时间（分钟）
    /// </summary>
    public int SessionTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// 审计日志保留天数
    /// </summary>
    public int AuditLogRetentionDays { get; set; } = 90;

    /// <summary>
    /// 默认用户角色
    /// </summary>
    public IList<string> DefaultUserRoles { get; set; } = new List<string> { "User" };

    /// <summary>
    /// 管理员角色名称
    /// </summary>
    public string AdminRoleName { get; set; } = "Admin";

    /// <summary>
    /// 是否允许匿名访问
    /// </summary>
    public bool AllowAnonymousAccess { get; set; } = false;
}
```

## 7. 使用示例

### 7.1 权限检查示例

```csharp
// 检查用户权限
var permissionContext = new PermissionContext
{
    UserId = "user-123",
    Roles = new[] { "developer", "content-editor" },
    ResourceType = ResourceType.Node,
    ResourceId = nodeId,
    RequestedPermission = Permission.Write
};

var permissionResult = await permissionChecker.CheckPermissionAsync(permissionContext);
if (!permissionResult.IsGranted)
{
    throw new UnauthorizedAccessException(permissionResult.Reason);
}

// 执行需要权限的操作
await editor.UpdateNodeContentAsync(nodeId, LodLevel.Detail, newContent);
```

### 7.2 审计日志记录示例

```csharp
// 记录节点更新事件
await auditLogService.LogEventAsync(new AuditEvent
{
    EventType = AuditEventType.NodeUpdated,
    UserId = securityContext.UserId,
    UserName = securityContext.UserName,
    ResourceType = "Node",
    ResourceId = nodeId,
    Action = "UpdateContent",
    Description = $"Node '{nodeId}' content was updated",
    Details = new Dictionary<string, object>
    {
        ["LodLevel"] = LodLevel.Detail,
        ["ContentLength"] = newContent.Length
    },
    IpAddress = securityContext.IpAddress
});
```

### 7.3 安全上下文管理示例

```csharp
// 设置安全上下文
var securityContext = new SecurityContext
{
    UserId = "user-123",
    UserName = "john.doe",
    Roles = new[] { "developer", "content-editor" },
    SessionId = GuidEncoder.ToBase64String(Guid.NewGuid()),
    IpAddress = "192.168.1.100",
    Claims = new Dictionary<string, object>
    {
        ["department"] = "Engineering",
        ["clearance_level"] = "L2"
    }
};

await securityContextProvider.SetContextAsync(securityContext);

// 获取当前安全上下文
var currentContext = await securityContextProvider.GetCurrentContextAsync();
if (currentContext != null)
{
    Console.WriteLine($"Current user: {currentContext.UserName}");
}
```

## 8. 安全最佳实践

### 8.1 权限设计原则

1. **最小权限原则**: 用户只应获得完成其工作所需的最小权限
2. **职责分离**: 敏感操作应需要多个角色的协作
3. **权限继承**: 合理设计权限层次结构，避免权限冗余
4. **定期审查**: 定期审查和更新用户权限

### 8.2 审计日志最佳实践

1. **完整性**: 记录所有重要的系统操作和变更
2. **不可篡改**: 确保审计日志的完整性和不可篡改性
3. **及时性**: 实时记录事件，避免延迟
4. **可查询**: 提供灵活的查询和分析能力

### 8.3 安全配置建议

```csharp
// 生产环境安全配置
var securityOptions = new SecurityOptions
{
    EnablePermissionCheck = true,
    EnableAuditLog = true,
    SessionTimeoutMinutes = 30,
    AuditLogRetentionDays = 365,
    AllowAnonymousAccess = false,
    AdminRoleName = "SystemAdmin"
};

// 开发环境安全配置
var devSecurityOptions = new SecurityOptions
{
    EnablePermissionCheck = false,
    EnableAuditLog = true,
    SessionTimeoutMinutes = 480,
    AuditLogRetentionDays = 30,
    AllowAnonymousAccess = true
};
```

## 9. 集成指南

### 9.1 依赖注入配置

```csharp
// 注册安全服务
services.Configure<SecurityOptions>(configuration.GetSection("Security"));
services.AddScoped<IPermissionChecker, PermissionChecker>();
services.AddScoped<ISecurityContextProvider, SecurityContextProvider>();
services.AddScoped<IAuditLogService, AuditLogService>();

// 注册安全中间件
services.AddScoped<SecurityMiddleware>();
```

### 9.2 中间件集成

```csharp
/// <summary>
/// 安全中间件
/// 自动进行权限检查和审计日志记录
/// </summary>
public class SecurityMiddleware
{
    private readonly IPermissionChecker _permissionChecker;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityContextProvider _securityContextProvider;

    public SecurityMiddleware(
        IPermissionChecker permissionChecker,
        IAuditLogService auditLogService,
        ISecurityContextProvider securityContextProvider)
    {
        _permissionChecker = permissionChecker;
        _auditLogService = auditLogService;
        _securityContextProvider = securityContextProvider;
    }

    public async Task<T> ExecuteWithSecurityAsync<T>(
        Func<Task<T>> operation,
        PermissionContext permissionContext,
        AuditEventType auditEventType,
        string description)
    {
        // 检查权限
        var permissionResult = await _permissionChecker.CheckPermissionAsync(permissionContext);
        if (!permissionResult.IsGranted)
        {
            await LogAuditEventAsync(auditEventType, description, false, permissionResult.Reason);
            throw new UnauthorizedAccessException(permissionResult.Reason);
        }

        try
        {
            // 执行操作
            var result = await operation();

            // 记录成功的审计事件
            await LogAuditEventAsync(auditEventType, description, true);

            return result;
        }
        catch (Exception ex)
        {
            // 记录失败的审计事件
            await LogAuditEventAsync(auditEventType, description, false, ex.Message);
            throw;
        }
    }

    private async Task LogAuditEventAsync(AuditEventType eventType, string description, bool success, string? errorMessage = null)
    {
        var securityContext = await _securityContextProvider.GetCurrentContextAsync();

        var auditEvent = new AuditEvent
        {
            EventType = eventType,
            UserId = securityContext?.UserId ?? "anonymous",
            UserName = securityContext?.UserName ?? "anonymous",
            Description = description,
            Success = success,
            ErrorMessage = errorMessage,
            IpAddress = securityContext?.IpAddress ?? string.Empty
        };

        await _auditLogService.LogEventAsync(auditEvent);
    }
}
```

---
**下一阶段**: [Phase5_EventSystem.md](Phase5_EventSystem.md)
