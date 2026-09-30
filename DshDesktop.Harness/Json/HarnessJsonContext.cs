using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Models.Rpc;
using System.Text.Json.Serialization;

namespace DshDesktop.Harness.Json;

/// <summary>协议序列化的源生成上下文；全部走显式类型信息，无反射。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionListRequest))]
[JsonSerializable(typeof(SessionCreateRequest))]
[JsonSerializable(typeof(SessionPromptRequest))]
[JsonSerializable(typeof(SessionCancelRequest))]
[JsonSerializable(typeof(SessionFollowRequest))]
[JsonSerializable(typeof(SessionPageRequest))]
[JsonSerializable(typeof(SessionProjectionsRequest))]
[JsonSerializable(typeof(SessionSelectModelRequest))]
[JsonSerializable(typeof(SessionModelCatalogRequest))]
[JsonSerializable(typeof(WorkspaceCreateRequest))]
[JsonSerializable(typeof(WorkspaceArchiveSessionRequest))]
[JsonSerializable(typeof(CommandExecuteRequest))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SessionListValue))]
[JsonSerializable(typeof(SessionCreateValue))]
[JsonSerializable(typeof(SessionAcceptedValue))]
[JsonSerializable(typeof(SessionPageValue))]
[JsonSerializable(typeof(SessionProjectionsValue))]
[JsonSerializable(typeof(SessionSelectModelValue))]
[JsonSerializable(typeof(SessionModelCatalogValue))]
[JsonSerializable(typeof(SessionModelSelectionWire))]
[JsonSerializable(typeof(ModelProviderGroupWire))]
[JsonSerializable(typeof(ModelCatalogModelWire))]
[JsonSerializable(typeof(ModelReasoningWire))]
[JsonSerializable(typeof(ModelReasoningEffortWire))]
[JsonSerializable(typeof(ModelCatalogFailureWire))]
[JsonSerializable(typeof(SessionSummaryWire))]
[JsonSerializable(typeof(SessionProjectionHintsWire))]
[JsonSerializable(typeof(WorkspaceCreateValue))]
[JsonSerializable(typeof(WorkspaceArchiveValue))]
[JsonSerializable(typeof(CredentialInfoWire))]
[JsonSerializable(typeof(Dictionary<string, CredentialInfoWire>))]
[JsonSerializable(typeof(CommandExecuteValue))]
[JsonSerializable(typeof(PermissionCatalogWire))]
[JsonSerializable(typeof(PermissionPresetOptionWire))]
[JsonSerializable(typeof(RpcRequestEnvelope))]
[JsonSerializable(typeof(MuxOpenMessage))]
[JsonSerializable(typeof(MuxCancelMessage))]
[JsonSerializable(typeof(StreamPayloadWire))]
[JsonSerializable(typeof(EventsResultRequest))]
public sealed partial class HarnessJsonContext : JsonSerializerContext;
