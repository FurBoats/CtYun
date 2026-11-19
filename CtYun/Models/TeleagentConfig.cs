using System.Text.Json.Serialization;

namespace CtYun.Models
{
    /// <summary>
    /// Teleagent 保活配置。operationToken 是唯一的登录凭证（约 29 天有效），
    /// 从浏览器登录 agent.teleai.com.cn 后在 Cookie 中获取。
    /// </summary>
    public class TeleagentConfig
    {
        [JsonPropertyName("operationToken")]
        public string OperationToken { get; set; }

        /// <summary>可选：指定要保活的实例 UUID；为空时取实例列表第一个。</summary>
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; }

        /// <summary>每隔多少秒强制断开并重连一次 WebSocket（与原版行为一致）。</summary>
        [JsonPropertyName("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;
    }

    /// <summary>Teleagent 云电脑实例（/teleagent-manager/v1/instance/list 的元素）。</summary>
    public class InstanceInfo
    {
        public string Id { get; set; }
        public string Uid { get; set; }
        public int Status { get; set; }
    }

    /// <summary>
    /// getConnectDataByToken 返回的连接信息：桌面 WS 隧道所需的全部参数。
    /// 字段与天翼云电脑 desk 后端保持一致（与原版 ConnectInfo 相同）。
    /// </summary>
    public class ConnectionInfo
    {
        public string DesktopId { get; set; }
        public DesktopInfo DesktopInfo { get; set; }

        /// <summary>SendInfo 118 应答用的 userId（优先取连接信息，其次实例 uid）。</summary>
        public string UserId { get; set; }

        /// <summary>SendInfo 118 应答用的 userName（取不到时为空）。</summary>
        public string UserName { get; set; }
    }
}
