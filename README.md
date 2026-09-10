# X-Plane 12 局域网便携 EFB

一个只在本地局域网工作的 iPad 移动地图 EFB。Windows 单文件桥接器同时接收 X-Plane 12 UDP、托管网页并通过 WebSocket 推送遥测；不需要公网、云服务器、DDNS、NAT 配置或端口转发。

> 仅用于飞行模拟，不得用于真实导航。

## 架构

```text
X-Plane 12（电脑）
   │ DATA/UDP：位置、速度、姿态等
   ▼
XPlaneEfbBridge.exe
   ├─ UDP 监听：0.0.0.0:49000（可配置）
   ├─ DATA 包解析与字段合并
   ├─ HTTP 静态网页：0.0.0.0:8080
   └─ WebSocket：ws://电脑IP:8080/ws
                 │
                 ▼
        同一 Wi-Fi 的 iPad Safari/PWA
```

网页和 WebSocket 由同一个 exe、同一个地址提供，因此不需要 CORS。浏览器不能直接监听 UDP，exe 是数据链路中必需的桥接组件。

## 功能

- 单文件 Windows x64 exe；目标电脑无需安装 Node.js 或 .NET。
- 原生托盘菜单、后台运行、开机自启、打开配置、重新加载、显示 iPad 地址。
- 默认 UDP 49000，支持一个或多个候选监听端口和可选 X-Plane 来源 IP 白名单。
- 自动识别合法 `DATA` 包和发送端 IP；托盘显示实际 XP12 来源、端口和在线 iPad 数。
- 解析 Data Output 行 3、4、17、20：IAS/TAS/地速、Mach/VVI/G、俯仰/滚转、真/磁航向、经纬度及 MSL/AGL 高度。
- 分包状态合并；缺少某一数据行不会影响其他字段；未知协议/行安全忽略。
- iPad Safari 响应式 UI、横竖屏、安全区和触摸操作；仪表、状态、地图按钮和抽屉采用适合平板阅读的放大字号与触摸尺寸。
- Leaflet 与旋转扩展本地打包；支持北向上/航向向上一键切换、飞机图标、2,000 点轨迹、跟随、平移、缩放和清轨迹。
- OpenStreetMap 主底图，以及合规自定义 XYZ 底图切换。
- WebSocket 自动重连、UDP 3 秒超时状态和诊断抽屉。
- PWA manifest；HTTPS 安全上下文下支持 service worker 应用壳缓存。
- 合规授权的通用 XYZ 航图 provider 插槽。

## 目录结构

```text
.
├─ windows-bridge/
│  ├─ Program.cs                   托盘、UDP、解析、Kestrel、WebSocket
│  ├─ XPlaneEfbBridge.csproj       单文件发布和网页资源内嵌
│  └─ README.md                    exe 构建说明
├─ public/
│  ├─ vendor/leaflet/              随 exe 内嵌的 Leaflet
│  ├─ vendor/leaflet-rotate/       MIT 许可的本地地图旋转扩展
│  ├─ assets/icon.svg
│  ├─ index.html
│  ├─ styles.css
│  ├─ app.js
│  ├─ manifest.webmanifest
│  └─ sw.js
├─ src/                            Node.js 本地开发/调试桥接器
│  ├─ xplane/parser.js
│  ├─ xplane/udpBridge.js
│  ├─ config.js
│  └─ server.js
├─ test/                           Node 解析与 UDP 集成测试
├─ .env.example                    Node 调试模式配置
└─ package.json
```

## 最终用户使用 exe

已构建的文件：

```text
windows-bridge\dist\XPlaneEfbBridge.exe
```

1. 将这个 exe 复制到固定目录，例如 `C:\Program Files\XPlaneEfbBridge` 或用户文档目录。
2. 双击运行。程序不会显示主窗口，会出现在 Windows 系统托盘。
3. 首次运行会创建 `%LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json` 并显示 iPad 访问地址。
4. Windows 防火墙弹窗出现时，只允许“专用网络”。
5. 右击托盘图标可打开网页/配置、显示访问地址、重新加载配置、启用开机自启或退出。

默认配置：

```json
{
  "UdpPorts": [49000],
  "WebPort": 8080,
  "SourceIp": "",
  "AuthorizedChartTileUrl": "",
  "AuthorizedChartAttribution": "",
  "CustomBaseMapName": "",
  "CustomBaseMapUrl": "",
  "CustomBaseMapAttribution": "",
  "NavigraphExternalUrl": "https://charts.navigraph.com/"
}
```

修改配置后从托盘选择“重新加载配置”。`SourceIp` 留空时接受局域网内任意发送端；如果网络中有多台模拟器，建议填写 X-Plane 电脑的固定局域网 IPv4。

`AuthorizedChartTileUrl` 会下发给 iPad 浏览器，因此不得在 URL 中放长期密钥；需要鉴权的图层必须使用供应商批准的浏览器 SDK 或合规本地代理。

如果已有获得授权、与 Leaflet 兼容的 XYZ 服务，可配置：

```json
"CustomBaseMapName": "我的底图",
"CustomBaseMapUrl": "https://tiles.example.com/{z}/{x}/{y}.png",
"CustomBaseMapAttribution": "© 服务提供商"
```

不要直接套用未获许可的地图瓦片地址；需要遵守服务商的使用和署名条款。

### 配置文件自动修复

配置文件必须只有一个根 JSON 对象。新版若发现文件中误拼接了两个对象，或一个合法对象后带有 `(` 等多余内容，会选择包含 `WebPort` 和 `UdpPorts` 的当前局域网配置，先将原文件备份为 `bridge-config.json.invalid-时间戳.bak`，再重写为合法 JSON。旧版的 `CloudUrl`、`SessionId`、`IngestToken` 和已经回退的天地图字段不会写入新配置。

### 关于默认 49000

按需求默认监听 UDP 49000。但 49000 也经常被 X-Plane 用作自身接收端口：如果 exe 和 X-Plane 在同一台电脑上出现“UDP 49000 不可用”或端口占用，请将配置改为：

```json
"UdpPorts": [49003]
```

同时把 X-Plane 的数据发送目标端口改成 49003。端口号本身没有特殊要求，两端一致即可。也可配置候选端口，例如 `[49003, 49002]`；程序会同时监听并自动显示首先收到合法 DATA 包的端口。UDP 没有服务发现握手，因此无法自动询问 X-Plane 当前输出端口。

## X-Plane 12 设置

1. 打开 **Settings → Data Output**。
2. 在下列行勾选最右侧 **Network via UDP**：
   - `Speeds`，常见索引 3；
   - `Mach, VVI, g-load`，常见索引 4；
   - `Pitch, roll, & headings`，常见索引 17；
   - `Latitude, longitude, & altitude`，常见索引 20。
3. 设置 UDP 接收目标：
   - X-Plane 与 exe 同机：目标 IP 使用 `127.0.0.1`；
   - X-Plane 与 exe 不同电脑：目标 IP 使用运行 exe 的电脑局域网 IPv4；
   - 目标端口使用桥接器 `UdpPorts` 中的一个，默认 49000。
4. 建议输出频率 5–10 Hz；更高频率通常不会改善移动地图体验。

字段和行名称以 X-Plane 当前设置界面为准。可参考 Laminar 的[官方 Data Set Output Table](https://www.x-plane.com/kb/data-set-output-table/)。

## iPad 访问

电脑和 iPad 必须：

- 连接同一个局域网/Wi-Fi；
- 不使用阻止设备互访的访客网络；
- Wi-Fi 路由器未启用 AP isolation/client isolation；
- 电脑网络类型设为 Windows“专用网络”。

托盘菜单会显示检测到的访问地址。也可在 PowerShell 查询 IPv4：

```powershell
Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object {$_.IPAddress -notlike '169.254*' -and $_.InterfaceAlias -notlike '*Loopback*'}
```

iPad Safari 打开，例如：

```text
http://192.168.1.25:8080
```

不要在 iPad 上使用 `localhost` 或 `127.0.0.1`，它们指向 iPad 自己。

## Windows 防火墙

首选首次运行时允许 exe 的“专用网络”访问。若需要显式端口规则，以管理员身份运行 PowerShell：

```powershell
New-NetFirewallRule -DisplayName "X-Plane EFB Web" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "X-Plane EFB UDP" -Direction Inbound -Protocol UDP -LocalPort 49000 -Action Allow -Profile Private
```

如果改用 49003，应同步修改第二条规则。不要为“公用网络”开放，也不要在路由器上配置端口转发。

## HTTP、WebSocket 和 PWA 限制

- 网页通过 `http://电脑IP:8080` 加载，并自动连接同源 `ws://电脑IP:8080/ws`；不存在 HTTPS 页面连接明文 WS 的 mixed-content 问题。
- 由于网页、API、WS 同源，不需要 CORS；不要把前端单独放到 GitHub Pages。
- 局域网 IP 上的普通 HTTP 通常不属于 Safari 安全上下文，service worker 不会注册，离线应用壳不可靠。
- Safari 仍可通过分享菜单尝试“添加到主屏幕”，但不同 iPadOS 版本对普通 HTTP 的安装/全屏行为可能不同。
- 如果以后希望获得完整 PWA 安装和 service worker，可在局域网内用受 iPad 信任的证书和反向代理提供 HTTPS；这不是当前运行的必要条件。
- OSM 和自定义在线瓦片仍需要互联网。飞机数据链路完全在局域网内，但没有互联网时底图可能为空。

### Shadowrocket 与 OSM

- Shadowrocket 开启后不一定影响局域网访问，关键取决于路由规则。应让 `192.168.0.0/16`、`10.0.0.0/8` 和 `172.16.0.0/12` 走 `DIRECT`，不要启用会把本地网络也强制送入隧道的选项。电脑热点地址 `192.168.43.5` 属于应直连的私网地址。
- EFB HTML、WebSocket 和飞机遥测来自电脑局域网地址；OSM 瓦片则由 iPad Safari 直接请求 `https://tile.openstreetmap.org`。因此电脑能访问 OSM 并不会自动替 iPad 加载瓦片。
- 推荐在 Shadowrocket 规则模式中让电脑局域网地址走 `DIRECT`，让 `tile.openstreetmap.org` 走 `PROXY`。这样 EFB 数据仍在局域网内，地图瓦片经 iPad 的代理节点访问。
- 也可在 `CustomBaseMapUrl` 中配置合法授权且 iPad 可访问的 XYZ 服务。不要随意搭建公共 OSM 代理；如确需由电脑代理瓦片，必须遵守 OSM 的缓存、标识、署名和使用量政策。

## Navigraph 合规限制

Navigraph 订阅本身不授予开发者 API 凭据或在自制 EFB 中展示数据的许可。Navigation Data API 可以向获批应用交付包含航点的完整数据包，但没有公开的“按航点查询”端点；把这些数据叠加成地图还可能触及禁止生成 chart-like rendering 的限制，必须在开发者审核中取得对具体展示方式的书面确认。Charts API 的许可更严格，官方原则上只批准模拟器进程内虚拟 EFB；独立 iPad/桌面 EFB 不应假定可用。

当前提供：

- 打开 Navigraph 官方 Charts 的外部入口；
- 界面明确提示 Navigraph API 当前未启用；
- `AuthorizedChartTileUrl` 通用 XYZ 图层插槽，仅供你拥有明确展示权利的服务使用；
- OSM 与该授权图层之间的图层切换。

订阅级别、端点、开发者申请、OIDC 流程以及可替代数据源的完整结论见 [`docs/NAVIGRAPH-COMPLIANCE.md`](docs/NAVIGRAPH-COMPLIANCE.md)。在获得批准之前，本项目不会下载、解析、代理或渲染 Navigraph 数据，也不会把 client secret 放进网页或公开配置。

## 构建单文件 exe

开发机要求 Windows 和 .NET 8 SDK：

```powershell
dotnet publish .\windows-bridge\XPlaneEfbBridge.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o .\windows-bridge\dist
```

网页、CSS、JavaScript、图标和 Leaflet 全部嵌入 exe。发布目录只需要复制 `XPlaneEfbBridge.exe`。正式分发建议使用受信任的代码签名证书签名，以改善 SmartScreen 体验。

## Node.js 开发模式

exe 是最终部署方式；Node.js 服务保留用于开发、自动测试和调试：

```powershell
npm install
Copy-Item .env.example .env
npm run demo     # 无需 XP12 的网页演示
npm start        # 接收真实 XP12 UDP
npm test
```

运行中的 `npm start` 或 `npm run demo` 使用 `Ctrl+C` 结束。不要同时运行 Node 服务和 exe，否则 Web 端口会冲突。

## 验收步骤

1. 双击运行 `XPlaneEfbBridge.exe`，确认托盘出现图标。
2. 右击托盘选择“在本机打开 EFB”，确认电脑浏览器能显示地图。
3. iPad 与电脑连接同一 Wi-Fi，打开托盘显示的 `http://电脑IP:8080`。
4. 网页顶部初始显示“等待 UDP”，抽屉中 WebSocket 应显示“已连接”。
5. 在 X-Plane 启用 DATA 行 3、4、17、20，并发送到电脑 IP和桥接 UDP 端口。
6. 托盘应显示 `XP12 发送端IP · UDP 端口 · 设备数`。
7. 网页在数秒内显示实时状态、飞机位置、GS、ALT、HDG 和 V/S。
8. 在 X-Plane 改变航向/高度，确认图标、数值和轨迹同步；点击“航向向上”，确认飞机朝上且地图随航向旋转，再次点击应恢复北向上。
9. 打开 `http://电脑IP:8080/api/status`，确认 `packets`、`parsed` 增长且 `hasPosition` 为 true。
10. 暂时关闭 Wi-Fi再恢复，确认 WebSocket 自动重连。

## 常见问题

**iPad 打不开网页**：确认 exe 仍在托盘；两台设备同一 Wi-Fi；使用电脑 IP而非 localhost；检查 TCP 8080 专用网络防火墙和路由器设备隔离。

**网页能打开但一直等待 UDP**：检查 X-Plane 目标 IP、端口和四个 Network via UDP 复选框；查看 `/api/status` 的 `packets` 是否增长。

**UDP 49000 被占用**：同机运行时较常见。把 exe 配置和 X-Plane 目标同时改成 49003。

**`packets` 增长但 `parsed` 为 0**：收到的不是标准 `DATA` 协议或只有不支持的行。确认 X-Plane 使用 Data Output，而不是其他插件自定义格式。

**只有部分数值**：对应 Data Output 行没有启用。桥接器会保留其他有效字段，这是预期容错行为。

**显示 UDP 数据超时**：三秒内没有新数据。检查 X-Plane是否暂停输出、电脑睡眠、Wi-Fi 丢包或防火墙策略。

**OSM 地图空白但飞机数值正常**：局域网遥测正常，但 iPad 无法访问 OSM 瓦片互联网服务。低流量个人使用仍需遵守 OSM 瓦片政策。

**JSON 提示 `invalid after a single JSON value`**：文件中存在第二个 JSON 对象或根对象后的多余文本。使用新版 exe 会自动备份并修复；修复后的文件应只包含一对最外层 `{}`。

**托盘选择“退出”后图标变灰、进程仍存在**：新版已将同步阻塞式关闭改为异步关闭，并会主动中止 WebSocket、释放 UDP、在 4 秒内停止 Kestrel 后结束消息循环。请确认正在运行的是 `windows-bridge\dist` 中的最新版 exe。

**出现多个访问 IP**：虚拟机/VPN可能创建额外网卡。新版 exe 会优先显示具有默认网关的 Wi-Fi/以太网地址；仍应选择和 iPad 同一网段的地址，例如手机热点网关是 `192.168.43.1` 时选择电脑的 `192.168.43.x`，不要选择 VMware/Hyper-V 虚拟网卡地址。

**exe 被 SmartScreen 提示**：自构建或未签名 exe 的常见现象。核对来源；正式分发请进行代码签名，不应指导用户绕过系统安全警告。

**配置后没有变化**：保存 JSON 后从托盘选择“重新加载配置”；检查 JSON 逗号和引号格式。

## 安全边界

服务监听 `0.0.0.0` 是为了让 iPad 访问。它没有登录鉴权，因此只适合可信家庭/训练局域网。不要配置路由器端口转发，不要在公网网络配置文件上放行规则；不可信局域网中应设置 `SourceIp` 并用 Windows 防火墙限制来源网段。
