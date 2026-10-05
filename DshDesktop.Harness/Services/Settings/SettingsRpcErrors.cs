using DshDesktop.Core.Exceptions;
using DshDesktop.Harness.Exceptions;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DshDesktop.Harness.Services.Settings;

/// <summary>
///     settings/credentials 域错误码到 Core 异常的映射。conflict 语义是重读重放而非格式错误；
///     credential/rejected 的 message 是后端 seam 原文，须原样展示。其余错误码映射失败返回 false，
///     由调用方以 throw; 透传原始异常，保留堆栈。
/// </summary>
internal static class SettingsRpcErrors
{
    public static bool TryMap(HarnessRpcException exception, [NotNullWhen(true)] out Exception? mapped)
    {
        switch (exception.Code)
        {
            case "settings/conflict" :
                mapped = new SettingsConflictException(exception.FindDetailString("ns") ?? string.Empty,
                                                       FindDetailInt64(exception.Details, "expected"),
                                                       FindDetailInt64(exception.Details, "actual"));
                return true;

            case "settings/rejected" :
                mapped = new SettingsRejectedException(exception.FindDetailString("ns") ?? string.Empty,
                                                       exception.RawMessage             ?? exception.Message);
                return true;

            case "credential/rejected" :
                mapped = new CredentialRejectedException(exception.FindDetailString("ref") ?? string.Empty,
                                                         exception.RawMessage              ?? exception.Message);
                return true;

            default :
                mapped = null;
                return false;
        }
    }

    /// <summary>从 details 中读取整数字段；缺失或形状不符返回 0（conflict 响应会携带 expected/actual）。</summary>
    private static long FindDetailInt64(JsonElement? details, string name)
    {
        if (details is not { ValueKind: JsonValueKind.Object } detailsElement ||
            !detailsElement.TryGetProperty(name, out var element)             ||
            element.ValueKind != JsonValueKind.Number                         ||
            !element.TryGetInt64(out var value))
            return 0;

        return value;
    }
}
