using System.Buffers;
using System.Text.Json;

namespace DshDesktop.Utils;

/// <summary>
///     手工构建不可变 JsonElement 的小型工厂：设置写入值不经反射序列化，
///     一律经 Utf8JsonWriter 写入缓冲后解析克隆（与模拟设置服务同一路径，Native AOT 安全）。
/// </summary>
public static class JsonElementFactory
{
    public static JsonElement FromString(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return Parse(buffer);
    }

    public static JsonElement FromBoolean(bool value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteBooleanValue(value);
        }

        return Parse(buffer);
    }

    public static JsonElement FromInt64(long value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
        }

        return Parse(buffer);
    }

    /// <summary>构建数组元素：调用方经 writer 逐项写入，本方法负责数组首尾与解析克隆。</summary>
    public static JsonElement FromArray(Action<Utf8JsonWriter> writeItems)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writeItems(writer);
            writer.WriteEndArray();
        }

        return Parse(buffer);
    }

    /// <summary>构建对象元素：调用方经 writer 逐属性写入，本方法负责对象首尾与解析克隆。</summary>
    public static JsonElement FromObject(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return Parse(buffer);
    }

    private static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
