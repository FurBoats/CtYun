using CtYun.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CtYun
{
    /// <summary>
    /// 天翼云电脑 desk 后端（desk.ctyun.cn:8810）会话层。
    /// 复刻网页客户端的协商加密：RSA 协商出 {eid, evalue} 后，
    /// 业务请求体用 AES-CBC（key=evalue 的 UTF-8 字节，IV=全零）加密。
    /// </summary>
    internal class DeskApi
    {
        private const string BaseUrl = "https://desk.ctyun.cn:8810";
        private const string Version = "204000200";
        private const string DeviceType = "60";

        private readonly HttpClient _client;
        private readonly string _deviceCode;
        private RSA _rsa;
        private string _eid;
        private string _evalue;
        private bool _encryptionEnabled;

        public DeskApi(string deviceCode)
        {
            _deviceCode = deviceCode;
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            _client = new HttpClient(handler);
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36");
            _client.DefaultRequestHeaders.Add("Origin", "https://pc.ctyun.cn");
            _client.DefaultRequestHeaders.Add("Referer", "https://pc.ctyun.cn/");
        }

        /// <summary>查询服务端加密开关并完成密钥协商。只需成功一次，后续调用自动复用。</summary>
        public async Task EnsureNegotiatedAsync(CancellationToken ct = default)
        {
            if (_eid != null)
            {
                return;
            }

            _encryptionEnabled = await QueryEncryptionEnabledAsync(ct);
            if (!_encryptionEnabled)
            {
                _eid = string.Empty;
                Utility.WriteLine(ConsoleColor.Yellow, "[desk] 服务端未启用报文加密，走明文模式。");
                return;
            }

            _rsa = RSA.Create(2048);
            var spki = _rsa.ExportSubjectPublicKeyInfo();
            var payload = "{\"etype\":\"2\",\"certType\":\"2\",\"certData\":\"" + Convert.ToBase64String(spki) + "\"}";
            var body = await PostPlainAsync("/api/auth/client/negotiationEncKey", payload, ct);

            using var doc = JsonDocument.Parse(body);
            var data = UnwrapData(doc.RootElement);
            var encKey = GetString(data, "encKey");
            var encData = GetString(data, "encData");
            if (string.IsNullOrEmpty(encKey) || string.IsNullOrEmpty(encData))
            {
                throw new Exception("negotiationEncKey 响应缺少 encKey/encData：" + Truncate(body));
            }

            var encKeyBytes = Convert.FromBase64String(encKey);
            // 网页端用 WebCrypto(RSA-OAEP/SHA-512) 生成密钥对、却用 JSEncrypt(PKCS#1 v1.5) 风格解密，
            // 两种填充都试，以能解开 encData 为准。
            var paddings = new (string Name, RSAEncryptionPadding Padding)[]
            {
                ("PKCS#1 v1.5", RSAEncryptionPadding.Pkcs1),
                ("OAEP-SHA512", RSAEncryptionPadding.OaepSHA512),
                ("OAEP-SHA256", RSAEncryptionPadding.OaepSHA256),
                ("OAEP-SHA1", RSAEncryptionPadding.OaepSHA1),
            };
            foreach (var (name, padding) in paddings)
            {
                string n;
                try
                {
                    n = Encoding.UTF8.GetString(_rsa.Decrypt(encKeyBytes, padding));
                }
                catch
                {
                    continue;
                }
                if (TryDecryptNegotiated(encData, n, out var eid, out var evalue))
                {
                    _eid = eid;
                    _evalue = evalue;
                    Utility.WriteLine(ConsoleColor.Green, $"[desk] 密钥协商成功（RSA 填充：{name}）。");
                    return;
                }
            }
            throw new Exception("negotiationEncKey 解密失败：所有 RSA 填充与 AES 密钥派生组合都无法解开 encData。");
        }

        /// <summary>用一次性 connectToken 换取桌面连接信息（connectionInfo）。</summary>
        public async Task<ConnectionInfo> GetConnectDataByTokenAsync(string connectToken, string fallbackUid, CancellationToken ct = default)
        {
            await EnsureNegotiatedAsync(ct);
            var payload = "{\"connectToken\":\"" + EscapeJson(connectToken) + "\",\"osType\":15,\"deviceType\":60,\"from\":\"_ai_target_\"}";
            var body = await PostAsync("/api/desktop/client/getConnectDataByToken", payload, ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number && codeEl.GetInt32() != 0)
            {
                throw new Exception($"getConnectDataByToken 业务错误：code={codeEl.GetInt32()} msg={GetString(root, "msg")}");
            }
            var data = UnwrapData(root);
            if (data.ValueKind != JsonValueKind.Object)
            {
                throw new Exception("getConnectDataByToken 响应 data 不是对象：" + Truncate(body));
            }
            return ParseConnectionInfo(data, fallbackUid);
        }

        /// <summary>查询桌面状态（与网页客户端行为一致，失败不影响保活）。</summary>
        public async Task GetDesktopStateAsync(string desktopId, CancellationToken ct = default)
        {
            try
            {
                await PostAsync("/api/desktop/client/state", "[" + desktopId + "]", ct);
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.DarkYellow, "[desk] 查询桌面状态失败（忽略）：" + ex.Message);
            }
        }

        private static ConnectionInfo ParseConnectionInfo(JsonElement data, string fallbackUid)
        {
            if (!data.TryGetProperty("desktopInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            {
                throw new Exception("connectionInfo 缺少 desktopInfo，实际字段：" + string.Join(",", data.EnumerateObject().Select(p => p.Name)));
            }
            var desktopInfo = new DesktopInfo
            {
                Host = GetString(info, "host"),
                Port = GetString(info, "port"),
                ClinkLvsOutHost = GetString(info, "clinkLvsOutHost"),
                CaCert = GetString(info, "caCert"),
                ClientCert = GetString(info, "clientCert"),
                ClientKey = GetString(info, "clientKey"),
                Token = GetString(info, "token"),
                TenantMemberAccount = GetString(info, "tenantMemberAccount")
            };
            if (string.IsNullOrEmpty(desktopInfo.ClinkLvsOutHost))
            {
                throw new Exception("desktopInfo 缺少 clinkLvsOutHost，实际字段：" + string.Join(",", info.EnumerateObject().Select(p => p.Name)));
            }
            var userId = GetString(data, "userId") ?? GetString(info, "userId") ?? fallbackUid;
            var userName = GetString(data, "userName") ?? GetString(info, "userName") ?? desktopInfo.TenantMemberAccount;
            return new ConnectionInfo
            {
                DesktopId = GetString(data, "desktopId"),
                DesktopInfo = desktopInfo,
                UserId = userId,
                UserName = userName
            };
        }

        private async Task<bool> QueryEncryptionEnabledAsync(CancellationToken ct)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Get, "/api/cdserv/client/getServData");
                using var response = await _client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                // 实测响应：{"code":0,"data":{"globalSwitches":{"bodyMsgEType":["2","3"],...}}}
                // bodyMsgEType 含 "2"（AES_CBC）即启用加密
                using var doc = JsonDocument.Parse(body);
                var data = UnwrapData(doc.RootElement);
                if (data.TryGetProperty("globalSwitches", out var switches) &&
                    switches.TryGetProperty("bodyMsgEType", out var etypes))
                {
                    if (etypes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in etypes.EnumerateArray())
                        {
                            if (item.ToString() == "2" || item.ToString() == "AES_CBC")
                            {
                                return true;
                            }
                        }
                        return false;
                    }
                    var single = etypes.ToString();
                    return single == "2" || single.Contains("AES_CBC");
                }
                return true;
            }
            catch
            {
                // 查询失败时按网页端默认行为尝试加密协商
                return true;
            }
        }

        private async Task<string> PostAsync(string path, string plainJson, CancellationToken ct)
        {
            if (!_encryptionEnabled)
            {
                return await PostPlainAsync(path, plainJson, ct);
            }
            var cipher = AesEncrypt(plainJson, _evalue);
            var wrapped = "{\"data\":\"" + cipher + "\"}";
            using var request = NewRequest(HttpMethod.Post, path);
            request.Headers.Add("ctg-reqdata-etype", "2");
            request.Headers.Add("ctg-nego-ekeyid", _eid);
            request.Content = new StringContent(wrapped, Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"desk 请求 {path} 失败 HTTP {(int)response.StatusCode}：{Truncate(body)}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("edata", out var edata) && edata.ValueKind == JsonValueKind.String)
            {
                return AesDecrypt(edata.GetString(), _evalue);
            }
            return body;
        }

        private async Task<string> PostPlainAsync(string path, string json, CancellationToken ct)
        {
            using var request = NewRequest(HttpMethod.Post, path);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"desk 请求 {path} 失败 HTTP {(int)response.StatusCode}：{Truncate(body)}");
            }
            return body;
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, BaseUrl + path);
            var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            request.Headers.Add("ctg-appmodel", "2");
            request.Headers.Add("ctg-devicetype", DeviceType);
            request.Headers.Add("ctg-version", Version);
            request.Headers.Add("ctg-softwarecode", "web_client");
            request.Headers.Add("ctg-devicecode", _deviceCode);
            request.Headers.Add("ctg-timestamp", timestamp);
            request.Headers.Add("ctg-requestid", timestamp);
            return request;
        }

        private static bool TryDecryptNegotiated(string encData, string n, out string eid, out string evalue)
        {
            eid = null;
            evalue = null;
            foreach (var key in KeyCandidates(n))
            {
                try
                {
                    var plain = AesDecrypt(encData, key);
                    using var doc = JsonDocument.Parse(plain);
                    var root = doc.RootElement;
                    eid = GetString(root, "eid");
                    evalue = GetString(root, "evalue");
                    if (!string.IsNullOrEmpty(eid) && !string.IsNullOrEmpty(evalue))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 换下一种密钥派生继续试
                }
            }
            return false;
        }

        /// <summary>encData 的 AES 密钥候选：字符串原文 / Base64 解码 / Hex 解码。</summary>
        private static IEnumerable<string> KeyCandidates(string n)
        {
            var candidates = new List<string> { n };
            try
            {
                var bytes = Convert.FromBase64String(n);
                if (bytes.Length is 16 or 24 or 32)
                {
                    candidates.Add(Encoding.GetEncoding("ISO-8859-1").GetString(bytes));
                }
            }
            catch { }
            if (n.Length % 2 == 0 && n.All(Uri.IsHexDigit))
            {
                var bytes = Convert.FromHexString(n);
                if (bytes.Length is 16 or 24 or 32)
                {
                    candidates.Add(Encoding.GetEncoding("ISO-8859-1").GetString(bytes));
                }
            }
            return candidates;
        }

        // FQ/F6 等价实现：AES-CBC + PKCS7，key = 密钥字符串的 UTF-8 字节，IV = 16 字节全零，密文 Base64。
        // 注意：KeyCandidates 可能返回 ISO-8859-1 映射的字符串，其 UTF-8 编码会变形；
        // 因此这里直接按字节处理，字符串先按 ISO-8859-1 还原成原始字节。
        private static string AesEncrypt(string plain, string keyString)
        {
            var key = KeyBytes(keyString);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length));
        }

        private static string AesDecrypt(string base64, string keyString)
        {
            var key = KeyBytes(keyString);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length));
        }

        private static byte[] KeyBytes(string keyString)
        {
            // 普通情形：evalue/n 本身就是 ASCII 密钥字符串，UTF-8 与原始字节一致。
            // 若字符串来自 ISO-8859-1 字节映射（码点 ≤255 且含非 ASCII），按字节还原。
            if (keyString.Any(c => c > 127))
            {
                return Encoding.GetEncoding("ISO-8859-1").GetBytes(keyString);
            }
            return Encoding.UTF8.GetBytes(keyString);
        }

        private static JsonElement UnwrapData(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                return data;
            }
            return root;
        }

        private static string GetString(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
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

        private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Truncate(string text) => text.Length <= 200 ? text : text.Substring(0, 200) + "...";
    }
}
