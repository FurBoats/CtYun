using CtYun;
using CtYun.Models;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");
Utility.WriteLine(ConsoleColor.Green, "Teleagent 云电脑保活（仅 operationToken 登录）");

if (args.Contains("--selftest"))
{
    // 无令牌冒烟：只验证 desk 协商加密链（getServData → negotiationEncKey → AES 往返）。
    var deskTest = new DeskApi("web_" + GenerateRandomString(32));
    try
    {
        await deskTest.EnsureNegotiatedAsync();
        Utility.WriteLine(ConsoleColor.Green, "[selftest] 密钥协商成功。");
        try
        {
            var info = await deskTest.GetConnectDataByTokenAsync("selftest-bogus-token", "0");
            Utility.WriteLine(ConsoleColor.Yellow, "[selftest] 假 token 竟然换到了连接信息？desktopId=" + info.DesktopId);
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.Green, "[selftest] 假 token 被拒（符合预期）：" + ex.Message);
        }
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, "[selftest] 密钥协商失败：" + ex.Message);
    }
    return;
}

var config = LoadConfig();
if (config == null || string.IsNullOrWhiteSpace(config.OperationToken))
{
    Utility.WriteLine(ConsoleColor.Red, "未读取到 operationToken。请在 config.json 中配置，或设置环境变量 TELEAGENT_OPERATION_TOKEN。");
    Utility.WriteLine(ConsoleColor.Yellow, "获取方式：浏览器登录 agent.teleai.com.cn 后，在 Cookie 中复制 operationToken 的值（约 29 天有效）。");
    return;
}
var keepAliveSeconds = Math.Max(10, config.KeepAliveSeconds);

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    globalCts.Cancel();
};

var teleagent = new TeleagentApi(config.OperationToken.Trim());
var desk = new DeskApi(ResolveDeviceCode());

List<InstanceInfo> instances;
try
{
    instances = await teleagent.GetInstancesAsync(globalCts.Token);
}
catch (TeleagentAuthException ex)
{
    Utility.WriteLine(ConsoleColor.Red, ex.Message);
    Utility.WriteLine(ConsoleColor.Red, "operationToken 已失效，请重新登录 Teleagent 后更新配置。");
    return;
}

if (instances.Count == 0)
{
    Utility.WriteLine(ConsoleColor.Yellow, "未获取到云电脑实例。");
    return;
}

var instance = instances.FirstOrDefault(i => string.Equals(i.Id, config.InstanceId, StringComparison.OrdinalIgnoreCase)) ?? instances[0];
Utility.WriteLine(ConsoleColor.Cyan, $"目标实例：{instance.Id}（uid={instance.Uid}，status={instance.Status}）");
if (instance.Status != 100)
{
    Utility.WriteLine(ConsoleColor.Yellow, $"实例状态不是运行中（status={instance.Status}），仍尝试连接；若失败请先在 Teleagent 界面确认云电脑已开机。");
}

Utility.WriteLine(ConsoleColor.Yellow, $"保活任务启动：每 {keepAliveSeconds} 秒强制重连一次。");
try
{
    await KeepAliveWorker(teleagent, desk, instance, keepAliveSeconds, globalCts.Token);
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}

async Task KeepAliveWorker(TeleagentApi teleagent, DeskApi desk, InstanceInfo instance, int keepAliveSeconds, CancellationToken globalToken)
{
    var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");

    while (!globalToken.IsCancellationRequested)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        sessionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveSeconds));

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
        client.Options.AddSubProtocol("binary");

        try
        {
            Utility.WriteLine(ConsoleColor.Cyan, $"[{instance.Id}] === 新周期开始，换取连接信息 ===");
            var connectToken = await teleagent.ConnectAsync(instance.Id, globalToken);
            var info = await desk.GetConnectDataByTokenAsync(connectToken, instance.Uid, globalToken);
            await desk.GetDesktopStateAsync(info.DesktopId, globalToken);
            Utility.WriteLine(ConsoleColor.Cyan, $"[{instance.Id}] 桌面 {info.DesktopId}，网关 {info.DesktopInfo.ClinkLvsOutHost}");

            var uri = new Uri($"wss://{info.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{info.DesktopId}/MAIN");
            await client.ConnectAsync(uri, sessionCts.Token);

            var hostParts = info.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
            var connectMessage = new ConnecMessage
            {
                type = 1,
                ssl = 1,
                host = hostParts[0],
                port = hostParts.Length > 1 ? hostParts[1] : "443",
                ca = info.DesktopInfo.CaCert,
                cert = info.DesktopInfo.ClientCert,
                key = info.DesktopInfo.ClientKey,
                servername = info.DesktopInfo.Host + ":" + info.DesktopInfo.Port,
                oqs = 0
            };

            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
            await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

            await Task.Delay(500, sessionCts.Token);
            await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

            Utility.WriteLine(ConsoleColor.Green, $"[{instance.Id}] 连接已就绪，保持 {keepAliveSeconds} 秒...");

            try
            {
                await ReceiveLoop(client, info, sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{instance.Id}] 周期时间到，准备重连...");
            }
        }
        catch (TeleagentAuthException ex)
        {
            Utility.WriteLine(ConsoleColor.Red, ex.Message);
            Utility.WriteLine(ConsoleColor.Red, "operationToken 已失效，请重新登录 Teleagent 后更新配置。程序退出。");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{instance.Id}] 异常: {ex.Message}");
            await Task.Delay(5000, globalToken);
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", CancellationToken.None);
                }
                catch { }
            }
        }
    }
}

async Task ReceiveLoop(ClientWebSocket ws, ConnectionInfo info, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) break;

        if (result.Count == 0)
        {
            continue;
        }

        var data = buffer.AsSpan(0, result.Count).ToArray();
        var hex = BitConverter.ToString(data).Replace("-", "");
        if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
        {
            Utility.WriteLine(ConsoleColor.Green, "-> 收到保活校验");
            var response = encryptor.Execute(data);
            await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
            Utility.WriteLine(ConsoleColor.DarkGreen, "-> 发送保活响应成功");
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var item in infos)
            {
                if (item.Type == 103)
                {
                    var userIdJson = long.TryParse(info.UserId, out var uidNum) ? uidNum.ToString() : "\"" + EscapeJson(info.UserId ?? "") + "\"";
                    var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + EscapeJson(info.UserName ?? "") + "\",\"userInfo\":\"\",\"userId\":" + userIdJson + "}");
                    var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.DarkYellow, $"消息解析失败: {ex.Message}");
        }
    }
}

TeleagentConfig LoadConfig()
{
    var dataDir = GetDataDir();
    Directory.CreateDirectory(dataDir);

    TeleagentConfig config = null;
    var configPath = Environment.GetEnvironmentVariable("TELEAGENT_CONFIG");
    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(dataDir, "config.json");
    }

    if (File.Exists(configPath))
    {
        try
        {
            var json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.TeleagentConfig);
            Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{configPath}");
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
        }
    }

    config ??= new TeleagentConfig();

    var envToken = Environment.GetEnvironmentVariable("TELEAGENT_OPERATION_TOKEN");
    if (!string.IsNullOrWhiteSpace(envToken))
    {
        config.OperationToken = envToken;
    }
    var envInstance = Environment.GetEnvironmentVariable("TELEAGENT_INSTANCE_ID");
    if (!string.IsNullOrWhiteSpace(envInstance))
    {
        config.InstanceId = envInstance;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("TELEAGENT_KEEP_ALIVE_SECONDS"), out var envKeepAlive))
    {
        config.KeepAliveSeconds = envKeepAlive;
    }

    if (string.IsNullOrWhiteSpace(config.OperationToken) && CanReadFromConsole())
    {
        Console.Write("operationToken: ");
        config.OperationToken = Console.ReadLine()?.Trim();
    }

    return config;
}

string ResolveDeviceCode()
{
    var dataDir = GetDataDir();
    var devicesDir = Path.Combine(dataDir, "devices");
    Directory.CreateDirectory(devicesDir);
    var deviceCodePath = Path.Combine(devicesDir, "teleagent.txt");
    if (!File.Exists(deviceCodePath))
    {
        File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
    }
    return File.ReadAllText(deviceCodePath).Trim();
}

string GetDataDir()
{
    var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        return dataDir;
    }
    return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
}

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

static bool CanReadFromConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

static bool IsRunningInContainer() => File.Exists("/.dockerenv");
