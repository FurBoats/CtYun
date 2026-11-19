# CtYun 云电脑保活（Teleagent 版）

基于 [leleji/CtYun](https://github.com/leleji/CtYun) 二次开发，**只支持中国电信 Teleagent（agent.teleai.com.cn）的云电脑保活**。原版天翼云电脑账号密码登录（验证码 OCR、短信绑定等）已全部移除。

## 原理

Teleagent 与天翼云电脑共用同一套 desk 后端与 CLINK 协议，保活链路为：

1. 用 `operationToken`（Teleagent 会话 Cookie）调 `instance/list` 找到云电脑实例，调 `instance/connect` 换取一次性 `connectToken`。
2. 与 `desk.ctyun.cn:8810` 做 RSA 协商加密（negotiationEncKey + AES-CBC），用 `connectToken` 调 `getConnectDataByToken` 换取桌面连接信息。
3. 连接 `wss://<网关>/clinkProxy/<桌面ID>/MAIN`，按原版协议完成 REDQ 校验应答与 SendInfo 应答；每 `keepAliveSeconds` 秒强制断开重连，并重新换取连接信息（connectToken 一次性）。

## 配置

程序在数据目录读取 `config.json`：

- 本机运行：默认数据目录为程序所在目录。
- Docker：默认数据目录为 `/app/data`，建议挂载到宿主机。
- 环境变量 `CTYUN_DATA_DIR` 可指定数据目录，`TELEAGENT_CONFIG` 可指定配置文件完整路径。

`config.json` 示例：

```json
{
  "operationToken": "从浏览器 Cookie 复制的 operationToken 值",
  "instanceId": "",
  "keepAliveSeconds": 60
}
```

- `operationToken`：**必填**。获取方式：浏览器登录 https://agent.teleai.com.cn/teleagent 后，打开开发者工具 → 应用/存储 → Cookie，复制 `operationToken` 的值。
- `instanceId`：可选。为空时自动选择实例列表中的第一个；账号下有多台云电脑时建议填 `instance/list` 返回的实例 `id`。
- `keepAliveSeconds`：可选，默认 60，最小 10。

也可以用环境变量覆盖：`TELEAGENT_OPERATION_TOKEN`、`TELEAGENT_INSTANCE_ID`、`TELEAGENT_KEEP_ALIVE_SECONDS`。未配置令牌时，程序会在控制台提示输入。

### 有效期说明

`operationToken` 约 29 天过期，且服务端没有刷新接口。过期后程序会明确报错退出，重新登录 Teleagent 并更新 `config.json` 中的 `operationToken` 即可（需要过一次登录滑块验证）。

## 运行

```bash
dotnet run --project CtYun
```

或使用仓库自带 Dockerfile 构建镜像，挂载数据目录后运行：

```bash
docker build -t ctyun-teleagent ./CtYun
docker run -d --name ctyun-teleagent -v /your/data:/app/data ctyun-teleagent
```

自检（无需令牌，只验证 desk 协商加密链是否可用）：

```bash
dotnet CtYun.dll --selftest
```

## 致谢

原项目 [leleji/CtYun](https://github.com/leleji/CtYun)（CLINK 协议保活核心、REDQ 校验应答实现均来自原版）。
