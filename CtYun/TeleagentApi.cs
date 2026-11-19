using CtYun.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CtYun
{
    /// <summary>operationToken 已失效（服务端返回 401/403），需要重新登录获取。</summary>
    public class TeleagentAuthException : Exception
    {
        public TeleagentAuthException(string message) : base(message) { }
    }

    /// <summary>
    /// Teleagent（agent.teleai.com.cn）会话层。
    /// 只凭 operationToken 一个 cookie 工作：列实例 → 为实例换取一次性 connectToken。
    /// </summary>
    internal class TeleagentApi
    {
        private const string BaseUrl = "https://agent.teleai.com.cn";
        private readonly HttpClient _client;

        public TeleagentApi(string operationToken)
        {
            var cookies = new CookieContainer();
            cookies.Add(new Uri(BaseUrl), new Cookie("operationToken", operationToken, "/", "teleai.com.cn"));
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            _client = new HttpClient(handler);
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36");
            _client.DefaultRequestHeaders.Add("Referer", "https://agent.teleai.com.cn/teleagent");
            // 前端在 x-token 缺失时会直接用 operationToken 顶替，这里保持一致。
            _client.DefaultRequestHeaders.Add("x-token", operationToken);
        }

        public async Task<List<InstanceInfo>> GetInstancesAsync(CancellationToken ct = default)
        {
            using var response = await _client.GetAsync(BaseUrl + "/teleagent-manager/v1/instance/list", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            EnsureAuthorized(response.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            CheckErrorCode(root);
            var list = new List<InstanceInfo>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    list.Add(new InstanceInfo
                    {
                        Id = GetString(item, "id"),
                        Uid = GetString(item, "uid"),
                        Status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0
                    });
                }
            }
            return list;
        }

        /// <summary>为指定实例换取一次性 connectToken（connectURL 中 token= 的值）。</summary>
        public async Task<string> ConnectAsync(string instanceId, CancellationToken ct = default)
        {
            var payload = "{\"id\":\"" + instanceId + "\"}";
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync(BaseUrl + "/teleagent-manager/v1/instance/connect", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            EnsureAuthorized(response.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            CheckErrorCode(root);
            if (!root.TryGetProperty("data", out var data))
            {
                throw new Exception("instance/connect 响应缺少 data：" + Truncate(body));
            }
            var connectUrl = GetString(data, "connectURL");
            if (string.IsNullOrEmpty(connectUrl))
            {
                throw new Exception("instance/connect 响应缺少 connectURL：" + Truncate(body));
            }
            return ExtractToken(connectUrl);
        }

        private static string ExtractToken(string connectUrl)
        {
            var idx = connectUrl.IndexOf("token=", StringComparison.Ordinal);
            if (idx < 0)
            {
                throw new Exception("connectURL 中未找到 token 参数");
            }
            var rest = connectUrl.Substring(idx + "token=".Length);
            var end = rest.IndexOf('&');
            if (end >= 0)
            {
                rest = rest.Substring(0, end);
            }
            return Uri.UnescapeDataString(rest);
        }

        private static void EnsureAuthorized(HttpStatusCode status, string body)
        {
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            {
                throw new TeleagentAuthException($"Teleagent 返回 {(int)status}，operationToken 已失效。响应：{Truncate(body)}");
            }
            if ((int)status < 200 || (int)status >= 300)
            {
                throw new Exception($"Teleagent 请求失败 HTTP {(int)status}：{Truncate(body)}");
            }
        }

        private static void CheckErrorCode(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            {
                var message = GetString(error, "message");
                if (message != null && (message.Contains("登录") || message.Contains("token", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new TeleagentAuthException("Teleagent 会话无效：" + message);
                }
                throw new Exception("Teleagent 接口错误：" + message);
            }
        }

        private static string GetString(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
            }
            return null;
        }

        private static string Truncate(string text) => text.Length <= 200 ? text : text.Substring(0, 200) + "...";
    }
}
