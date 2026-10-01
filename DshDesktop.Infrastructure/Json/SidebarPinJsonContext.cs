using DshDesktop.Infrastructure.Models;
using System.Text.Json.Serialization;

namespace DshDesktop.Infrastructure.Json;

/// <summary>置顶配置文件的源生成序列化上下文（Native AOT 下无反射）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SidebarPinState))]
internal sealed partial class SidebarPinJsonContext : JsonSerializerContext;
