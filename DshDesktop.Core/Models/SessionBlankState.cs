namespace DshDesktop.Core.Models;

/// <summary>
///     会话空白状态的三级置信度，来源对齐参考实现 sessionListMetadata 投影：
///     <see cref="ConfirmedBlank" /> 是 wire 明确携带空白元数据（blank=true，含新会话
///     seq=0 的可靠回退）；<see cref="Engaged" /> 是已确认出现 turn/start（wire 元数据
///     blank=false，或本端发送被接受、观察到运行/内容等过渡信号）；<see cref="Unknown" />
///     是元数据缺失或读取失败的保守回退（wire blank=false 且不带 sessionListMetadata），
///     不得解释为已确认有内容，也不得猜测隐藏。
/// </summary>
public enum SessionBlankState
{
    ConfirmedBlank,
    Unknown,
    Engaged
}
