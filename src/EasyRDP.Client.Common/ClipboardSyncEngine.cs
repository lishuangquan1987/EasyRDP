using System;
using EasyRDP.Core.Protocol;

namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 客户端剪贴板同步引擎。
    /// 双向同步 + 500ms 静默期防止死循环。
    /// 不负责实际的剪贴板读写——通过 IClipboardProvider 委托给 UI 层。
    /// </summary>
    public class ClipboardSyncEngine
    {
        private string _lastSentText;
        private DateTime _cooldownUntil;
        private static readonly TimeSpan CooldownDuration = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 检查本地剪贴板变更并编码为协议消息。
        /// 返回 null 表示：无变化、在静默期内、或文本为空。
        /// </summary>
        /// <param name="currentText">当前剪贴板文本</param>
        /// <param name="sequence">消息序号</param>
        /// <returns>编码后的字节数组，无需发送则返回 null</returns>
        public byte[] TryEncodeLocalChange(string currentText, uint sequence)
        {
            if (currentText == null)
                currentText = string.Empty;

            // 未变化
            if (currentText == _lastSentText)
                return null;

            // 静默期内（收到远程剪贴板后短时间不发送本地变更）
            if (DateTime.Now < _cooldownUntil)
                return null;

            // 编码
            var msg = new ClipboardDataMessage
            {
                Format = ClipboardFormat.UnicodeText,
                Text = currentText
            };

            _lastSentText = currentText;

            return MessageCodec.Encode(MessageType.ClipboardData, sequence, msg);
        }

        /// <summary>
        /// 处理收到的远程剪贴板数据。
        /// 返回应写入本地剪贴板的文本；如果在静默期内则返回 null。
        /// 调用方应使用 IClipboardProvider 将返回的文本写入本地剪贴板。
        /// </summary>
        public string OnRemoteClipboard(ClipboardDataMessage msg)
        {
            if (msg == null || msg.Format != ClipboardFormat.UnicodeText)
                return null;

            // 启动静默期，防止本地检测到变更后立即回传
            BeginCooldown();

            // 更新"上次发送"文本，防止本地监控误检测为变更
            _lastSentText = msg.Text;

            return msg.Text;
        }

        /// <summary>
        /// 重置状态（断连时调用）。
        /// </summary>
        public void Reset()
        {
            _lastSentText = null;
            _cooldownUntil = DateTime.MinValue;
        }

        /// <summary>
        /// 启动 500ms 静默期。
        /// </summary>
        public void BeginCooldown()
        {
            _cooldownUntil = DateTime.Now + CooldownDuration;
        }
    }
}
