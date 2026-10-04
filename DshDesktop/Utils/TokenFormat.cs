namespace DshDesktop.Utils;

/// <summary>token 计数的展示格式化（对齐参考 Web 客户端 token-format.ts 的口径）。</summary>
public static class TokenFormat
{
    /// <summary>紧凑计数：517 / 12.2K / 517K / 1.2M。</summary>
    public static string Compact(long value)
    {
        return value switch
        {
            < 1_000     => value.ToString(),
            < 1_000_000 => $"{Scaled(value / 1_000.0)}K",
            _           => $"{Scaled(value / 1_000_000.0)}M"
        };
    }

    /// <summary>
    ///     缓存命中百分数：取整显示；部分命中取整会虚高到 100 时保留一位小数，
    ///     完整命中显示 100。分母为零（无计费输入）返回 null。
    /// </summary>
    public static string? CacheHitPercent(long cacheReadTokens, long billedInputTokens)
    {
        if (billedInputTokens <= 0) return null;

        var percent = cacheReadTokens * 100.0 / billedInputTokens;
        if (percent >= 99.95 && cacheReadTokens < billedInputTokens) return percent.ToString("0.0");

        return ((int)Math.Round(percent)).ToString();
    }

    /// <summary>生成速度：23.5 tok/s。</summary>
    public static string TokensPerSecond(double tokensPerSecond)
    {
        return $"{tokensPerSecond:0.#} tok/s";
    }

    private static string Scaled(double value)
    {
        return value >= 100 ? ((int)Math.Round(value)).ToString() : $"{value:0.#}";
    }
}
