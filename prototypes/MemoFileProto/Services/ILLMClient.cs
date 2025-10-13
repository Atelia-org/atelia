using MemoFileProto.Models;

namespace MemoFileProto.Services;

/// <summary>
/// LLM 客户端统一接口（Provider 无关）
/// </summary>
public interface ILLMClient : IDisposable {
    /// <summary>
    /// 流式聊天补全
    /// </summary>
    /// <param name="request">Provider 无关的通用请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式响应增量</returns>
    IAsyncEnumerable<UniversalResponseDelta> StreamChatCompletionAsync(
        UniversalRequest request,
        CancellationToken cancellationToken = default
    );
}
