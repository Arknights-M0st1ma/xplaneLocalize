# Train Sim World 6 遥测接入调研与集成方案

> 调研日期：2026-09-12
> **评审与修订日期：2026-09-13**（本版已按代码事实与公开规范逐条校正，改动处标注 **⚠ 修正（2026-09-13）**；并新增 §6.1–§6.4、§9.4）
> 对象：Train Sim World 6（TSW6，PC）→ 现有 `XPlaneEfbBridge.exe` → 网页端 Leaflet 地图
> 用途：仅用于模拟器娱乐。本方案不涉及真实铁路、不用于真实调度或导航。
>
> 全文凡是不确定的信息都标注为 **需验证**，没有编造的端点或字段。
> 修订依据：本仓库代码（带行号）+ 公开的逆向 OpenAPI 3.1 规范（<https://tsw-inspector.waaghals.dev/openapi.yaml>）。**官方 PDF 仍未能获取**，因此 §3.4 的字段名一律保留"需验证"标记。

## 0. 先明确假设

| 编号 | 假设 | 如果假设不成立会怎样 |
| --- | --- | --- |
| A1 | 目标是 PC 版 TSW6（Steam / Epic），且游戏与 `XPlaneEfbBridge.exe` 运行在**同一台 Windows 电脑**上 | 官方明确说明 API 不支持主机平台；跨机部署需要额外验证 API 是否监听非回环网卡（见 3.1） |
| A2 | 集成方式限定为「完全基于当前已有的 exe」，即不新增 Node/Python/其它运行时、不要求玩家装 Mod | 若允许装 Mod，可选方案更多（见 2.4） |
| A3 | 前端地图仍是现有的 Leaflet + 内嵌网页（同源，8080 端口），不改成独立站点 | 若前端变成独立域名，会引入 CORS 问题（见 3.8、4） |
| A4 | 「实时」的可接受延迟是 0.2–1 s，刷新率 4–10 Hz | 更高频率需要重新评估 API 与轮询开销（见 3.7） |
| A5 | 用户能接受「在 Steam 启动项加一个参数」这类一次性设置 | 不能接受就必须走 Mod / 屏幕读取等替代路径 |

## 1. 结论摘要

**结论：可行（属于「官方支持的接口 + 需要一层中间件」）。**

TSW6 确实内置了一个**官方 HTTP API**（Dovetail 官方在 2025-10-08 的论坛帖里正式介绍，并附了官方 PDF《TSW External Interface API 1.5 1》）。它提供本机 HTTP + JSON 接口，可以读取列车速度、位置经纬度、坡度、前方限速与信号等信息，也可以反向写入控制量。

对本项目最重要的三点：

1. 位置是**真实经纬度**（`geoLocation.latitude/longitude`，WGS84 量级），可以直接丢给 Leaflet，不需要做世界坐标到地理坐标的换算。
2. 速度单位是**米/秒（m/s）**，需要自行换算成 km/h 或 mph。
3. API **只有 HTTP 轮询，没有 WebSocket / SSE**；推送层必须由我们自己提供——正好是现有的 exe 已经在做的事（HTTP + WebSocket `/ws` + 内嵌前端）。

因此推荐架构是：**TSW6 → 官方 HTTP API（`127.0.0.1:31270`）→ 现有 exe 内的 TSW 采集/转换模块 → 复用现有 WebSocket `/ws` → 现有 Leaflet 网页**。全部改动都落在这个 exe 里，符合「完全基于当前已经有的 exe」的要求。

## 2. 问题一：TSW6 有没有官方 HTTP API

### 2.1 有，而且是官方支持的

Dovetail Games 社区经理 DTG Alex 于 2025-10-08 在官方论坛发帖《Dovetail Games Train Sim World Api Support》，文中引用执行制作人 Matt Peddlesden 的说明：

- API 的目的就是让外部应用和硬件直接访问列车内部状态（官方原话：*"Read the speed of your train, set the throttle lever, change the weather"*）；
- 协议是 **HTTP over TCP + JSON 响应**；
- **主机（console）平台不支持**，仅 PC；
- 官方已确认有厂商在用：TSControllers（TSW6 原生支持）、ThirdRails（实时雷达/实时地图）、ProCab Simulators；
- 帖子里附带官方文档：`TSW External Interface API 1.5 1.pdf`（833 KB，需登录论坛账号才能下载）。

来源：<https://forums.dovetailgames.com/threads/train-sim-world-api-support.94488/>

### 2.2 启用方式（TSW6 实测路径）

1. Steam → TSW6 → 属性 → 启动选项，加入 `-HTTPAPI`；
2. 启动游戏一次；
3. 游戏会在下列位置生成 API 密钥文件：

```text
%USERPROFILE%\Documents\My Games\TrainSimWorld6\Saved\Config\CommAPIKey.txt
```

社区也确认了 TSW6 的这条路径与「必须加 `-HTTPAPI` 才会生成密钥」的行为（有用户不生成，重装后恢复，说明这是游戏侧的不稳定点）。

来源：

- Steam 讨论《API Key》（TSW6，2025-11-13）：<https://steamcommunity.com/app/3656800/discussions/0/682985658886843643/>
- ThirdRails 帮助页（TSW5 的同类说明）：<https://www.beensoft.nl/ThirdRails/Help/TrainSimWorldAPI.html>
- `TheJAG/tsw_connect`（TSW6 路径写死在代码里）：<https://github.com/TheJAG/tsw_connect>

### 2.3 官方文档与逆向文档

| 文档 | 性质 | 可访问性 |
| --- | --- | --- |
| `TSW External Interface API 1.5 1.pdf` | **官方**（Dovetail 随论坛帖发布） | 需要 Dovetail 论坛登录才能下载；**不要把它复制进本仓库** |
| ThirdRails《TSW API — Unofficial API Documentation》 | 第三方整理，按 ThirdRails 实际调用的接口反推 | 公开 PDF：<https://thirdrails.org/Downloads/TSW_API_Unofficial_Documentation.pdf> |
| `tsw-inspector` OpenAPI 3.1 规范 | 第三方逆向，MIT | <https://tsw-inspector.waaghals.dev/openapi.yaml>，仓库 <https://github.com/waaghals/tsw-inspector> |

社区在 2025-09 就讨论过「官方文档还没公开」，并拿 ThirdRails 的用法当参考；随后 2025-10 官方文档正式随帖发布。来源：<https://forums.dovetailgames.com/threads/api-documentation.93807/>

### 2.4 社区插件 / Mod / 中间件 / 内存读取的现状

| 方案 | 类型 | 现状 | 是否推荐 |
| --- | --- | --- | --- |
| 官方 HTTP API | 游戏内置 | 官方支持，PC 专用 | **推荐** |
| `TSW WebSocket API`（作者 Luex） | 游戏 Mod，往游戏里注入 uWebSockets 服务器，供第三方应用取数 | 2025-01 发布于 Train Sim Community，面向 TSW5 时代；**是否支持 TSW6 需验证** | 备选（要装 Mod，违背 A2） |
| ThirdRails | 商业/社区成品应用，自带实时地图与雷达 | 已在用官方 API 跟踪 TSW5/TSW6 | 替代品/竞品，不是数据源 |
| `tsw_connect`、`tsw6-realtime-weather` | 第三方客户端示例（Python / C#） | 都在直连官方 API，可直接抄思路 | 参考实现 |
| 内存读取（UE4/UE5 指针扫描、UE4SS 等） | 逆向 | 官方已提供正规接口，没有必要；且易被游戏更新破坏、可能违反用户协议/反作弊条款（是否有反作弊保护 **需验证**） | **不推荐** |
| 屏幕 OCR / 图像识别 | 视觉 | 只能拿到 HUD 上可见的数字，拿不到经纬度和限速前瞻 | 不推荐 |

## 3. 问题二：API 的具体细节

### 3.1 监听地址与端口

| 项目 | 值 | 证据强度 |
| --- | --- | --- |
| 协议 | HTTP + JSON（不是 HTTPS） | 官方帖 + 逆向文档 |
| 基址 | `http://127.0.0.1:31270/`（社区文档写作 `http://localhost:31270/`） | TSW5 逆向文档 + 两个 TSW6 客户端都写死这个地址 |
| 端口 | **31270**（固定，**是否可配置需验证**） | 同上 |
| 绑定网卡 | 已知客户端都用 `127.0.0.1`，按**仅回环**处理 | **需验证**：官方未说明是否监听 `0.0.0.0`；若只监听回环，采集程序必须与游戏同机 |
| 是否可远程访问 | 未知 | 安全性上建议始终按「仅本机」设计 |

### 3.2 端点一览

来自第三方逆向文档与公开 OpenAPI 规范；`{node}` 段用点号连接，例如 `DriverAid.PlayerInfo`。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/info` | 返回 `Meta`（`Worker`、`GameName`、`GameBuildNumber`、`APIVersion`、`GameInstanceID`）与 `HttpRoutes`（可用路由清单）。**⚠ 修正（2026-09-13）：`/info` 并不在逆向 OpenAPI 规范里**（该规范以一行 `# TODO /info` 结尾），它只有第三方客户端与论坛帖支持。因此**不能**把它当作唯一的存活探针：本项目的实现改为「TCP 可达 → `/list` 返回 `Result:Success`」两级探测，`/info` 仅作为附加信息（存在就打印 `Meta`，不存在就跳过）。详见 §9.4 |
| GET | `/list` | 递归列出可用节点路径 |
| GET | `/list/{node}` | 列出某节点的子节点与 endpoints（含 `Writable` 标记） |
| GET | `/get/{node}.{endpoint}` | 读取一个 endpoint 的当前值 |
| PATCH | `/set/{node}.{endpoint}?Value=…` | 写入（仅可写 endpoint，例如 `DriverAid`、`Pantograph_F.Value`） |
| GET | `/subscription?Subscription={id}` | 读取某订阅号的当前值集合 |
| POST | `/subscription/{path}?Subscription={id}` | 创建订阅（把若干 endpoint 登记进一个订阅号） |
| DELETE | `/subscription/?Subscription={id}` | 取消订阅 |
| GET | `/listsubscriptions` | 列出当前所有订阅 |

统一响应外壳：

```json
{ "Result": "Success", "Values": { "...": "..." } }
```

失败时可能是 `403`（密钥无效/被拒）或 `400`（参数问题）。

**⚠ 修正（2026-09-13）——这一条很重要**：除了非 2xx 状态码，**失败还可能以 HTTP 200 返回**，外壳是 `{"Result":"Error","Message":"…"}`（逆向规范里 `Get` 响应就是 `oneOf: [Get, Error]`，两者都在 200 上）。因此**判定成功必须看 `Result == "Success"`，只看状态码会静默地把错误当成数据**。

已核实的错误响应（来自公开的逆向 OpenAPI 3.1 规范）：

| 场景 | HTTP | 响应体 |
| --- | --- | --- |
| 密钥缺失或错误 | **403** | `{"errorCode":"dtg.comm.InvalidKey","errorMessage":"API Key for request doesn't match CommAPIKey.txt in the game config directory."}` |
| 订阅号不存在 | **400** | `{"errorCode":"dtg.comm.NoSuchSubscription","errorMessage":"Could not find requested subscription ID"}` |
| 端点/参数问题 | **200** | `{"Result":"Error","Message":"…"}` |

注意：第三方逆向文档里有一句「订阅的用法尚不明确，能创建但取不到值」，而 `tsw_connect`（TSW6）与 `tsw6-realtime-weather`（TSW6）都在实际使用订阅并取到了数据。**不同版本/不同路径的订阅行为需验证**；MVP 建议先用简单 GET 轮询，稳定后再考虑订阅。

### 3.3 认证方式

- 每个请求都要带请求头：`DTGCommKey: <CommAPIKey.txt 里的内容>`；
- 密钥来自游戏自己生成的 `CommAPIKey.txt`（读取时要去掉首尾空白/BOM）；
- 没有 OAuth、没有会话、没有 cookie；
- **密钥属于敏感信息**：只留在本机 exe 内，绝不能下发给浏览器或写进日志。

### 3.4 与本需求直接相关的字段

| 需要的量 | 端点 | 字段 | 单位/坐标系 |
| --- | --- | --- | --- |
| 列车位置（经纬度） | `GET /get/DriverAid.PlayerInfo` | `Values.geoLocation.longitude` / `.latitude` | 度，WGS84 量级，可直接给 Leaflet |
| 位置（备选） | `GET /get/CurrentDrivableActor.LatLon` | 返回经纬度 | 文档注明「随车辆不同而结构可能不同」**需验证** |
| 游戏瓦片坐标 | 同上 `PlayerInfo` | `Values.currentTile.x` / `.y` | 游戏内部瓦片网格，**投影未公开，仅作诊断用** |
| 列车速度 | `GET /get/CurrentDrivableActor.Function.HUD_GetSpeed` | 例如 `{ "Speed (ms)": 23.42 }` | **米/秒** |
| 当前限速 / 下一处限速 / 线路最大速度 | `GET /get/DriverAid.Data` | `speedLimit.value`、`nextSpeedLimit.value`、`trackMaxSpeed.value`、`serviceMaxSpeed.value`、`formationMaxSpeed.value` | **米/秒**；`3.4028e+38` 这类超大值代表「未定义」 |
| 距下一处限速 / 下一信号的距离 | 同上 | `distanceToNextSpeedLimit`、`distanceToSignal` | 米 |
| 前方限速/信号前瞻数组 | 同上 | `nextSpeedLimits[]`、`nextSignals[]` | 米 + 世界坐标 `{x,y,z}` |
| 坡度 | 同上 | `gradient` | 与 HUD 一致的坡度值 |
| 信号显示 | 同上 | `signalAspectClass`（如 `Clear`）、`signalSeen`、`speedLimitSeen`、`bSignalIsPermissive` | 枚举/布尔 |
| 车次/服务名 | `PlayerInfo` | `currentServiceName` | 字符串 |
| 玩家信息 | `PlayerInfo` | `playerProfileName`、`cameraMode` | 字符串 |
| 编组长度 | `GET /get/CurrentFormation.FormationLength` | `FormationLength` | 米（示例 200） |
| 机车/车辆类型 | `GET /get/CurrentFormation/{index}.ObjectClass` | `ObjectClass`，如 `RVM_CRG_DB_BR101_C` | 字符串 |
| 场景时间 | `GET /get/TimeOfDay.data` | `LocalTimeISO8601`、`SunPositionAzimuth`、`OriginLatitude`、`OriginLongitude` 等 | ISO8601；还顺带给出**线路原点经纬度** |

来源：ThirdRails 非官方文档（第 2–7 页）、`tsw_connect` 的字段映射、`tsw6-realtime-weather` 的 C# 模型（`Tsw6PlayerInfoValues` / `Tsw6GeoLocation` / `Tsw6CurrentTile`）。

### 3.5 坐标系统

| 坐标类型 | 出现位置 | 是否可用于 Leaflet |
| --- | --- | --- |
| 经纬度 | `DriverAid.PlayerInfo.Values.geoLocation`，`CurrentDrivableActor.LatLon`，`Timetable/{id}.LatLon` | **可以**，WGS84 |
| 游戏瓦片 | `currentTile {x, y}` | 不可以，投影未公开（**需验证**） |
| 世界坐标 | `nextSpeedLimitPosition {x,y,z}`、`nextSignalPosition {x,y,z}` | 不能直接用；若要在地图上画「前方限速点」，需要用相邻采样的经纬度做局部线性拟合（近似） |

需要注意：不同线路的「经纬度贴合真实世界」的程度不同（真实线路通常可靠，虚构/魔改线路可能偏差很大）——**逐条线路需验证**。

### 3.6 速度单位

- API 里的速度类数值是 **m/s**（`tsw_connect` 里 `MS_TO_MPH = 2.2369…` 就是干这个用的）；
- 换算：`km/h = m/s × 3.6`，`mph = m/s × 2.23694`；
- 现有前端显示的是 `groundSpeedKt`（节）。**建议在 exe 里换算出 `groundSpeedKmh` 并把 `kt` 字段也一并给出**，前端只挑一个显示，避免改动前端公式。

### 3.7 刷新频率

| 事实 | 数值 | 来源 |
| --- | --- | --- |
| API 是否有推送 | **没有**，只有请求/响应（无 WebSocket、无 SSE） | 官方帖、逆向文档的路由表 |
| 第三方调用频率 | ThirdRails 作者表示「每秒多次、连续跑一个多小时都没问题」 | 官方论坛帖回复 |
| `tsw_connect` 轮询频率 | 100 ms（10 Hz），使用订阅 | 仓库源码 |
| `tsw6-realtime-weather` | 默认每 5 s 检查一次位置（配置项） | 仓库 README |
| 游戏内部状态的真实刷新率 | **需验证** | — |

建议：采集端按 **4–10 Hz** 轮询，通过 WebSocket 推给浏览器；浏览器不需要自己发请求。

### 3.8 CORS / 跨域

**需验证，且大概率不支持。** 理由：

- 认证必须用自定义请求头 `DTGCommKey`，浏览器会先发 `OPTIONS` 预检；
- 只有游戏返回了 `Access-Control-Allow-Origin` 与 `Access-Control-Allow-Headers: DTGCommKey`，前端才可能直连；
- 逆向文档和官方帖都没有提到 CORS 头。

因此方案设计上应当**默认前端不直连 API**，而是由同源的 exe 代理（见 4、5）。验证命令见第 9 节。

## 4. 问题三：三种集成方式在当前项目里的可行性

| 方案 | 做法 | 可行性 | 判断理由 |
| --- | --- | --- | --- |
| 前端直连 API | 页面里的 JS 直接 `fetch('http://127.0.0.1:31270/...')` | **基本不可行** | ① CORS 预检几乎肯定失败（3.8）；② 页面是 HTTP、API 也是 HTTP，但本地网段访问还会被浏览器按「不安全上下文/私有网络访问（PNA）」拦截；③ 必须把 `DTGCommKey` 交给浏览器，等于把密钥暴露给任何能打开这个网页的人；④ 平板（iPad）根本连不到游戏机的 `127.0.0.1` |
| 后端代理（在 exe 里做） | exe 轮询 API，只把结果通过 `/ws` 推给前端，并额外提供 `/api/tsw/*` | **完全可行，推荐** | 与现有架构一模一样（现在就是 exe 收 UDP、推 `/ws`）；密钥不出本机；同源无 CORS；平板只跟 exe 打交道；轮询频率、退避、去重都可以集中控制 |
| 本地中间件（额外进程/Mod） | 再写一个 Node/Python 服务或装 WebSocket Mod | **可行但没必要** | 违背 A2；多一个要维护、要打包、要占端口的进程；只有「必须给第三方程序提供数据」时才值得 |

一句话：**exe 代理是唯一同时满足「安全、同源、无需新运行时」的方案。**

## 5. 问题四：推荐架构与数据流（完全基于现有 exe）

```text
┌────────────────────────── 同一台 Windows 电脑 ──────────────────────────┐
│                                                                        │
│  Train Sim World 6（Steam，启动参数 -HTTPAPI）                          │
│    ├─ 生成的密钥：Documents\My Games\TrainSimWorld6\Saved\Config\        │
│    │                CommAPIKey.txt                                      │
│    └─ 内置 HTTP API：http://127.0.0.1:31270/                            │
│                    ▲                                                   │
│                    │ GET /info  /get/DriverAid.PlayerInfo               │
│                    │ GET /get/CurrentDrivableActor.Function.HUD_GetSpeed │
│                    │ GET /get/DriverAid.Data                            │
│                    │ Header: DTGCommKey: <key>                          │
│                    │                                                   │
│  XPlaneEfbBridge.exe（现有单文件 exe，新增一个采集源）                    │
│    ├─ TswApiClient        轮询 31270，处理密钥、退避、超时                │
│    ├─ TswTelemetry        字段归一化：m/s→km/h、经纬度、限速、信号         │
│    ├─ BridgeWeb（现有）   把状态并进 snapshot → 广播 {type:"telemetry"}    │
│    ├─ Kestrel（现有）     0.0.0.0:8080：内嵌网页 + /api/* + /ws          │
│    └─ WinForms 托盘/设置窗口（现有，新增「TSW」分组）                      │
│                    │                                                   │
└────────────────────┼───────────────────────────────────────────────────┘
                     │ WebSocket ws://<电脑IP>:8080/ws
                     ▼
        同一 Wi-Fi 的 iPad / 浏览器（现有 Leaflet 页面，零 CORS）
```

数据流（每一步都复用现有能力）：

1. **TSW6 → API**：游戏内部把状态暴露在 `127.0.0.1:31270`。
2. **API → 采集**：exe 用带 `DTGCommKey` 的 `HttpClient` 以 4–10 Hz 轮询 2–3 个端点（位置、速度、可选的限速/信号）；任何一次失败都进入指数退避，并保留上一帧（标记为过期）。
3. **采集 → 归一化**：换算单位、合并字段成现有前端已经认识的形状（`latitude`、`longitude`、`groundSpeedKmh`/`groundSpeedKt`、`limitKmh`/`nextLimitKmh`/`distanceToNextLimitM`/`signalAspect`、`protocol: "TSW-API"`、`source: "127.0.0.1:31270"`、`receivedAt`）。**不产生 `headingTrueDeg`**（见 §6.4：列车模式不显示 HDG）。
4. **归一化 → 广播**：直接调用现有 `BridgeWeb.Publish(telemetry)`，走现有 `Channel` → 每个已连接的 WebSocket 客户端；客户端接入时先收到一次完整 snapshot（现有逻辑）。
5. **广播 → 地图**：`public/app.js` 现有分支 `message.type === 'telemetry'` 原样工作；飞机图标换成列车图标，单位换 km/h，新增「下一限速/信号」提示。

### 5.1 需要在 exe 里做的改动清单

| 文件 | 改动 | 备注 |
| --- | --- | --- |
| `windows-bridge/TswApiClient.cs` | **新增**：`HttpClient`（`BaseAddress = http://127.0.0.1:31270`，默认头 `DTGCommKey`），方法 `IsApiAvailableAsync()`、`GetPlayerInfoAsync()`、`GetSpeedAsync()`、`GetDriverAidAsync()`；密钥发现与缓存；指数退避 | 参考 `GarethLowe/tsw6-realtime-weather` 的 `Tsw6ApiClient.cs` 结构 |
| `windows-bridge/TswTelemetrySource.cs` | **新增**：`System.Threading.Timer` 循环 + 字段归一化 + 断线状态 | 与现有 UDP 采集平级 |
| `windows-bridge/Program.cs` | 采集源按配置二选一（或并存），调用现有的 `web.Publish(...)`；`/api/status` 增加 `tsw` 段 | 复用 `Publish` 与 `snapshot`，不新建推送通道 |
| `windows-bridge/ConfigStore.cs` | 新增键：`TelemetrySource`、`TswApiUrl`、`TswApiKeyPath`、`TswPollHz` | 现有读写/备份/未知键保留逻辑不用动，只需加进 `KnownKeys`（**⚠ 修正 2026-09-13：没有"导入导出映射"这回事，原方案此处描述有误**） |
| `windows-bridge/SettingsForm.cs` | 新增「TSW」分组：启用开关、端口、密钥路径（默认自动）、轮询频率、测试按钮 | 复用现有端口校验/掩码/测试按钮模式 |
| `public/app.js` + `index.html` + `styles.css` | 列车图标、km/h 显示、限速/信号卡片、按 `protocol` 切换字段标签 | 轨迹 canvas 图层、抽稀、航向向上旋转、重连、侧栏都可原样复用 |

**不需要改动的部分**：WebSocket 服务、静态网页托管、地图与瓦片、轨迹绘制与抽稀、SimBrief/天气模块、PWA、托盘与开机自启。

## 6. 问题五：前端实现要点

| 主题 | 做法 | 现有代码是否可以复用 |
| --- | --- | --- |
| 实时标记 | 用 `latitude/longitude` 更新同一个 `L.marker`，用 CSS `rotate()` 旋转图标（机头朝下/朝上的约定保持与飞机一致） | 是，`updateHeading()` 等逻辑直接沿用 |
| 轨迹 | 复用现有独立 canvas 图层、三层描边、起点标记 | 是 |
| 轨迹抽稀与上限 | 复用现有「最近 N 点保持全分辨率 + 较早部分按倍数抽稀」策略；列车 200 km/h ≈ 55 m/s，10 Hz 采样 6 小时约 21.6 万点，**必须依赖抽稀** | 是，但建议把默认上限调低一档 |
| 速度显示 | 统一在 exe 里换算，前端只显示 `groundSpeedKmh`（列车惯用 km/h）；保留 kt 字段以便复用现有格式化函数 | 部分 |
| 限速/信号提示 | 新增卡片行：当前限速、下一限速、距下一限速距离、信号显示；数据来自 `DriverAid.Data` | 否，新增 |
| 坐标转换 | **不需要**（API 直接给经纬度）。若要在图上标「前方限速点」，用最近两次采样的经纬度做局部线性外推近似，并明确标注为估算 | 否，可选 |
| 航向 | API 未见直接的航向字段（**需验证**），且**已决策：列车模式不显示 HDG**（见 §6.4），因此不做方位角推算 | 不需要 |
| 断线重连 | 三层：① exe 侧对 API 的退避重试；② 过期检测（`receivedAt` 超过约 3 s 就标注「信号丢失」，沿用现在 UDP 3 s 超时的交互）；③ 浏览器侧现有 WebSocket 自动重连不变 | 是 |
| 性能 | 浏览器不发轮询请求；WebSocket 推送频率与采集频率一致（4–10 Hz）；canvas 画轨迹避免 DOM 节点膨胀 | 是 |

> **⚠ 修正（2026-09-13）——`Publish` 没有"合并后只发最新"的语义。**
> 代码事实：`Program.cs:1238` 每次 `Publish` 都直接 `updates.Writer.TryWrite(…)`，`BroadcastLoop`（`Program.cs:1375-1383`）逐条读取、逐条向每个客户端发送。也就是说 **N 个客户端 × f Hz = N×f 条 WebSocket 消息/秒，没有合并、没有丢帧**。
> 影响与结论：单帧约 100–200 字节，4 Hz 完全可承受，**不需要为此改代码**；但采集频率就是实际推送频率，所以默认值取 **4 Hz**（可配 1–10 Hz），不要默认 10 Hz。

### 6.1 采集源切换必须清空快照（原方案遗漏）

`LocalWebServer.Publish`（`Program.cs:1234`）的合并是**只增不减**的：

```csharp
foreach (var item in telemetry) state[item.Key] = item.Value;
```

这带来两个必须处理的后果：

1. **陈旧字段污染**：如果先跑过 X-Plane，`state` 里会有 `altitudeMslFt`、`headingTrueDeg`、`groundSpeedKt`。切到 TSW 源后 TSW 不写高度，`altitudeMslFt` 会**保留上一次 X-Plane 会话的旧值**；而 `ApplyVerticalSpeed()`（`Program.cs:1245-1266`）每次 `Publish` 都读它，于是**持续用一个假高度喂最小二乘估计器，并把一个完全虚构的 `verticalSpeedFpm` 写回快照**。
2. **协议字段互相覆盖**：两个源并存时 `protocol`/`source` 每帧互相覆盖，前端 `setStatus` 与 `#sourceDiagnostic`（`app.js:540-541`）会闪烁。

因此实现必须包含：
- `LocalWebServer` 增加一个清空快照的方法（`ClearSnapshot()`），在**首次连接成功、采集源切换、服务重启**时调用；
- **不实现"两个源并存"**：配置项是二选一（见 §6.2），从根上避免覆盖；
- 快照里带上明确的来源标识（`protocol: "TSW-API"`），前端据此决定显示哪套字段。

### 6.2 数据源是二选一，不是并存（决策）

`TelemetrySource` 取值 `xp`（默认）或 `tsw`。**不支持同时接收**，因为两个源都会写 `latitude`/`longitude`/`protocol`，并存只会让地图上的图标来回跳。切换方式是在设置窗口里改并「重新加载配置（重启服务）」。

### 6.3 列车模式下必须隐藏的既有功能（原方案遗漏）

前端现有的三大功能在列车模式下语义错误，必须按 `protocol` 隐藏，这比"换个图标"的工作量大：

| 功能 | 列车模式处理 | 理由 |
| --- | --- | --- |
| SimBrief 航路（侧栏整块 + 航路图层） | **隐藏** | 飞行计划对铁路无意义 |
| 🛬 机场地面图层（`apt.dat`） | **隐藏**（开关与 `/api/ground/*` 请求都停） | 那是机场滑行道数据，不是铁路 |
| 航司徽章（`airlines.json` + `AirlineLogoUrlTemplate`） | **隐藏** | 呼号/航司对列车无意义 |
| OpenWeatherMap 天气图层 | **保留** | 真天气对驾驶有意义 |
| 航班状态浮层 | **替换**为列车状态（车次、限速、下一限速距离、信号） | 见 §6.4 |

### 6.4 仪表在列车模式下的映射（决策：不保留 HDG）

原方案（§6 表）把"航向"列为"部分复用"，并把航向计算放在最后一步"加料"。**修正后的决策是：列车模式不显示 HDG**，因为 API 是否有航向字段仍未验证，而用相邻点外推的方位角在停车、调车、折返时会产生误导性的数值。

| 仪表面板 | 飞机模式 | 列车模式 |
| --- | --- | --- |
| `GS` | `groundSpeedKt`（节） | `groundSpeedKmh`（km/h） |
| `ALT` | `altitudeMslFt`（FT MSL） | **替换为「限速」**（`limitKmh`，来自 `DriverAid.Data.speedLimit.value`） |
| `HDG` | `headingTrueDeg`（TRUE） | **不显示**（整个仪表隐藏） |
| `V/S` | `verticalSpeedFpm`（FPM） | **替换为「距下一限速」**（`distanceToNextLimitM`，米；无数据时显示 `—`） |

`headingTrueDeg` 因此**不需要**在列车模式下推算，前端的 `updateHeading()` / 航向向上逻辑在列车模式下不参与（列车模式下「航向向上」按钮不作朝向依据）。
| 多客户端 | 现有 `viewers` 计数与广播机制不变；采集只有一份，与观看人数无关 | 是 |
| 演示/无游戏 | 现有 `--demo` 思路可扩展成「TSW 模拟数据源」，方便在没有游戏时开发前端 | 部分 |

## 7. 问题六：MVP 步骤、技术选型与代码结构

### 7.1 技术选型

| 环节 | 选择 | 理由 |
| --- | --- | --- |
| 采集端 | 现有 C# `XPlaneEfbBridge.exe`（.NET 的 `HttpClient` + `System.Text.Json`） | 零新增运行时；单文件发布；与现有 Kestrel/托盘/设置界面同进程 |
| 传输（游戏→exe） | HTTP 轮询（先 `/get`，稳定后再评估 `/subscription`） | API 本身没有推送 |
| 传输（exe→浏览器） | 现有 WebSocket `/ws` | 已经实现并有自动重连 |
| 前端 | 现有 Leaflet + 现有轨迹 canvas 图层 | 经纬度可直接用 |
| 密钥 | 只读本机 `CommAPIKey.txt`，进程内缓存，不下发 | 安全 |

### 7.2 实施步骤（**已按"开发机 ≠ 游戏机"重排，2026-09-13**）

> 背景：本项目的开发机与装有 TSW6 的机器**不是同一台**，所以"先实测再写代码"的顺序不可行。下面的顺序把**能离线验证的部分全部提前**，把必须接触游戏的部分集中到最后一次性做完。

**阶段 0：离线可验证（不需要游戏）**
1. **`--tsw-probe` 诊断开关**：仿照现有 `--dump-udp` / `--dump-ofp`（`Program.cs:32-74`）加一个 CLI 开关，输出「可达性、`Meta`、`/list` 完整端点清单、候选端点原始 JSON」到文本文件。这是没有游戏时**唯一**能拿到的真实结构证据。
2. **`TswApiClient` + 归一化纯函数 + `EFB_TSW_BASE` 覆盖**：`EFB_TSW_BASE` 允许把客户端指向任意地址，做法与现有 `EFB_OWM_BASE`（`windows-bridge/README.md:73-74`）完全一致。
3. **Node 假 API + 单元测试**：`test/tsw-fake-api.js` 复刻 `Result` 外壳、`DTGCommKey`、`403 dtg.comm.InvalidKey`、**HTTP 200 + `Result:Error`**、`3.4028e+38` 哨兵、`Speed (ms)` 这种带空格括号的键名；`test/tsw-fake.test.js` 覆盖这些场景。**用它验证 C# 客户端的密钥发现、超时、退避、容错解析。**

**阶段 1：前端可在无游戏时开发（纯 JS）**
4. **`npm run demo` 支持列车形状**：让 demo 遥测输出 `protocol: "TSW-API"` 的帧（新增 `EFB_DEMO=tsw`），于是列车图标、km/h、§6.3 的功能隐藏、§6.4 的仪表映射**全部可以现在就在浏览器里开发和验证**。

**阶段 2：接入采集循环（用假 API 可半验证）**
5. **`TswTelemetrySource`**：默认 **4 Hz**（可配 1–10 Hz），轮询 2–3 个端点，归一化后 `web.Publish`，失败指数退避并保留上一帧（标记为过期）。
6. **`LocalWebServer.ClearSnapshot()`**（§6.1）：首次连接成功、源切换、重启时清空，避免陈旧字段污染。
7. **`/api/status` 增加 `tsw` 段**；`/api/tsw/status` 供前端诊断抽屉使用。

**阶段 3：配置与设置界面**
8. 4 个配置键进 `KnownKeys`；`SettingsForm` 新增「TSW」页签（启用开关、API 地址、密钥路径、轮询频率、测试按钮），并同步更新 `--settings-preview` 的页签索引文档（新页签会改变索引）。

**阶段 4：需要游戏时一次性做（对照 §9.2 的 10 条清单）**
9. 用 `--tsw-probe` 一次性确认：坐标质量、**是否有航向字段**、限速/信号字段名、订阅是否可用、端口可否改、CORS 是否放开。
10. 据实测结果决定是否引入 `/subscription` 优化（无它则请求数 ×3）。

### 7.3 示例代码结构

采集端（放在 exe 内，示意，非最终实现）：

```csharp
// windows-bridge/TswApiClient.cs
internal sealed class TswApiClient : IDisposable
{
    private readonly HttpClient client;

    public TswApiClient(int port, string? keyPath)
    {
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(1) };
        // 密钥只留在本机；读取失败时不要抛给 UI，交给 IsAvailable 判断
        if (TryReadKey(keyPath, out var key)) client.DefaultRequestHeaders.Add("DTGCommKey", key);
    }

    private static bool TryReadKey(string? explicitPath, out string key)
    {
        foreach (var path in KeyCandidates(explicitPath))
        {
            if (!File.Exists(path)) continue;
            key = File.ReadAllText(path).Trim().TrimStart('\uFEFF');
            if (key.Length > 0) return true;
        }
        key = string.Empty;
        return false;
    }

    private static IEnumerable<string> KeyCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath!;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(docs, "My Games", "TrainSimWorld6", "Saved", "Config", "CommAPIKey.txt");
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive))
            yield return Path.Combine(oneDrive, "Documents", "My Games", "TrainSimWorld6", "Saved", "Config", "CommAPIKey.txt");
    }

    public async Task<bool> IsAvailableAsync(CancellationToken token)
    {
        var info = await client.GetFromJsonAsync<JsonElement>("/info", token);
        return info.TryGetProperty("Meta", out var meta)
            && meta.TryGetProperty("Worker", out var worker)
            && worker.GetString() == "DTGCommWorkerRC";
    }

    public Task<JsonElement> GetPlayerInfoAsync(CancellationToken token) =>
        client.GetFromJsonAsync<JsonElement>("/get/DriverAid.PlayerInfo", token);

    public Task<JsonElement> GetSpeedAsync(CancellationToken token) =>
        client.GetFromJsonAsync<JsonElement>("/get/CurrentDrivableActor.Function.HUD_GetSpeed", token);

    public void Dispose() => client.Dispose();
}
```

归一化（把 API 的字段翻译成现有前端认识的形状）：

```csharp
// windows-bridge/TswTelemetrySource.cs
private Dictionary<string, object> Map(JsonElement player, JsonElement speed, JsonElement? driverAid)
{
    var values = player.GetProperty("Values");
    var geo = values.GetProperty("geoLocation");
    var metersPerSecond = speed.TryGetProperty("Speed (ms)", out var s) ? s.GetDouble() : double.NaN;

    return new Dictionary<string, object>
    {
        ["latitude"] = geo.GetProperty("latitude").GetDouble(),
        ["longitude"] = geo.GetProperty("longitude").GetDouble(),
        ["groundSpeedKmh"] = metersPerSecond * 3.6,
        ["groundSpeedKt"] = metersPerSecond * 1.943844,   // 兼容现有前端字段
        ["currentServiceName"] = values.TryGetProperty("currentServiceName", out var n) ? n.GetString() ?? "" : "",
        ["protocol"] = "TSW-API",
        ["source"] = "127.0.0.1:31270",
        ["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        // 可选：从 DriverAid.Data 补 limitKmh / nextLimitKmh / distanceToNextLimitM / gradient / signalAspect
    };
}
```

前端（`public/app.js`）改动点，尽量贴着现有写法：

```js
function updateTelemetry(data) {
  const isTrain = data.protocol === 'TSW-API';
  // 现有：$('#gs').textContent = format(data.groundSpeedKt ?? ...)
  $('#gs').textContent = isTrain
    ? formatUnit(data.groundSpeedKmh, 'km/h')
    : format(data.groundSpeedKt ?? data.trueAirspeedKt ?? data.indicatedAirspeedKt);

  if (!Number.isFinite(data.latitude) || !Number.isFinite(data.longitude)) return;
  // 轨迹与标记完全沿用现有实现：canvas 图层、抽稀、航向向上旋转
  pushTrackPoint(data.latitude, data.longitude);
}
```

## 8. 问题七：风险、限制、合规与替代方案

### 8.1 风险与限制

| 类别 | 风险 | 影响 | 缓解 |
| --- | --- | --- | --- |
| 接口稳定性 | API 是「官方提供但主要面向厂商」的接口，官方 PDF 仍需登录获取，版本号为 1.5 | 游戏更新后端点/字段可能变化 | 全部解析都容错；启动时打 `/info` 校验 `Meta`；字段缺失只影响对应显示 |
| 版本差异 | 逆向文档基于 TSW5，TSW6 字段名基本一致但不是同一版本记录 | 个别字段名不同 | 关键字段做「多候选名」兜底；用第 9 节清单逐条实测 |
| 订阅语义 | 有第三方报告「能创建订阅但取不到值」，同时又有两个 TSW6 客户端在正常用 | 若订阅不可用，请求数翻倍 | MVP 先用 `/get` 轮询，订阅作为优化项 |
| 密钥文件 | 有用户即使加了 `-HTTPAPI` 也不生成密钥（重装后恢复） | 用户装不上 | 设置窗口给出明确引导与「打开密钥文件夹」按钮；`/api/status` 明确区分「游戏没开」与「密钥没读到」 |
| 平台 | 官方明确主机不支持 API | PS/Xbox 用户完全无法使用 | 在文档与界面里讲清楚「仅 PC」 |
| 网络 | API 已知只在回环地址可访问 | 采集程序必须与游戏同机 | 与现在「exe 跟 X-Plane 同机」的模式一致，不增加新要求 |
| 坐标系 | 经纬度贴合真实世界的程度随线路而异 | 少数线路位置偏移大 | 在 UI 标注数据来源；必要时允许用户手动加偏移（不建议默认做） |
| 性能 | 长时间高频率轮询 + 长轨迹 | 游戏侧或浏览器卡顿 | 4–10 Hz、单进程单连接、轨迹抽稀、断线退避 |
| 字段语义 | `3.4028e+38` 等哨兵值、`CurrentDrivableActor.LatLon` 结构随车变化 | 显示成荒唐的数字 | 所有数值做范围校验（与现有解析器里过滤 `-999` 的做法一致） |
| 无航向 | API 未见列车航向字段（**需验证**） | 图标朝向 | **已决策：列车模式不显示 HDG，也不推算方位角**（§6.4）；列车图标固定朝上，不使用「航向向上」 |
| 陈旧字段污染 | `Publish` 的合并是只增不减的，切换数据源后旧源的字段会残留（`altitudeMslFt` 会让升降率估计器持续输出假值） | 地图/仪表显示错误数据 | `ClearSnapshot()` + 数据源二选一（§6.1、§6.2） |
| 假失败 | 失败可能以 HTTP 200 + `{"Result":"Error"}` 返回 | 只判断状态码的客户端会把错误当数据 | 一律以 `Result == "Success"` 判定成功 |

### 8.2 合规注意事项

1. **优先用官方 API**：官方帖明确说这是为了「无需 hack 或非标准手段」而提供的接口，用它就避开了内存读取、注入等灰区手段。
2. **不要把 API 密钥发给浏览器或写进日志/仓库**。密钥是用户本机凭据，`bridge-config.json` 里也不应存明文（更安全的做法是像现在处理代理密码那样用 DPAPI 加密，或干脆只存路径、每次运行时读游戏生成的文件）。
3. **不要复制官方 PDF 进仓库**：官方文档在论坛登录后才能下载，属于受限资料；仓库里只放链接与自写摘要。
4. **不要复制第三方文档全文**：本文件中的字段信息是事实性归纳，来源已标注；如要引用大段内容请核对第三方授权。
5. **不要用本项目做真实铁路相关的事**：沿用现有「仅供模拟飞行/模拟驾驶娱乐」的免责声明。
6. **如果将来要展示他人位置**（例如多人雷达），要注意第三方平台上他人的数据授权与隐私；本 MVP 只显示本机玩家自己的列车。

### 8.3 替代方案

| 方案 | 何时考虑 |
| --- | --- |
| 官方 API + exe 代理（本方案） | 默认选择 |
| 装 `TSW WebSocket API` Mod，exe 直接连它的 WebSocket | 如果希望**游戏侧主动推送**、且愿意让用户装 Mod；TSW6 兼容性 **需验证**：<https://www.trainsimcommunity.com/mods/c3-train-sim-world/c109-other/i5723-tsw-web-socket-api> |
| 参考现成成品 | 只想「看地图」而不想自己维护：ThirdRails（<https://thirdrails.org/>）已实现实时地图与雷达 |
| 屏幕 OCR | 只在完全拿不到 API 时才考虑，功能上限低 |
| 内存读取 / Mod 注入 | **不推荐**：官方已有正规接口，收益为零、风险很高 |

## 9. 问题八：最终结论与下一步验证清单

### 9.1 结论

**可行。** 更精确地说：

- **数据可得性：可行。** TSW6（PC）内置官方 HTTP API，`127.0.0.1:31270`，`DTGCommKey` 鉴权，直接提供列车经纬度与 m/s 速度，还附带限速、信号、坡度、编组等有用信息。
- **在现有地图模块里集成：可行，且应走 exe 代理。** 前端直连基本不可行（CORS/预检、密钥暴露、平板够不到回环地址）；exe 代理与现有架构完全同构。
- **「完全基于现有 exe」：可行。** 新增两个 C# 类 + 四个配置键 + 设置窗口一个分组 + 前端若干显示改动；推送通道、地图、轨迹、托盘全部复用。
- 唯一的硬约束是：**必须是 PC 版，且玩家要在 Steam 启动项加 `-HTTPAPI`。** 这一点无法绕过，也不该绕过。

### 9.2 下一步验证清单（每一条都能在 10 分钟内做完）

| # | 要验证的事 | 怎么做 | 通过标准 |
| --- | --- | --- | --- |
| 1 | TSW6 API 是否可用、版本信息 | `curl -s -H "DTGCommKey: <key>" http://127.0.0.1:31270/info` | 返回 `Meta.GameName = "Train Sim World 6®"`、`Meta.Worker = "DTGCommWorkerRC"` |
| 2 | 位置字段与是否随车移动 | `curl -s -H "DTGCommKey: <key>" http://127.0.0.1:31270/get/DriverAid.PlayerInfo` | `Values.geoLocation.latitude/longitude` 开动后变化 |
| 3 | 速度字段与单位 | `curl -s -H "DTGCommKey: <key>" "http://127.0.0.1:31270/get/CurrentDrivableActor.Function.HUD_GetSpeed"` | 返回 `{"Speed (ms)": …}`，与 HUD 的 km/h 对比 = ×3.6 |
| 4 | 限速/信号前瞻 | `curl -s -H "DTGCommKey: <key>" http://127.0.0.1:31270/get/DriverAid.Data` | `nextSpeedLimit.value`、`nextSignals[]`、`gradient` 有值 |
| 5 | 是否有航向字段 | `curl -s -H "DTGCommKey: <key>" http://127.0.0.1:31270/list/CurrentDrivableActor` 与 `…/list/DriverAid` | 在 endpoints 列表里找 `…Heading…`/`…Rotation…`。**注意：即使找到也不使用** —— 列车模式已决策不显示 HDG（§6.4），此项仅为完整记录 API 能力 |
| 6 | 坐标质量 | 把第 2 步的经纬度贴到 OSM，并分别在 2–3 条不同线路上重复 | 落点与实际线路走向一致（偏差 < 数十米） |
| 7 | CORS 是否可能放开 | `curl -i -X OPTIONS -H "Origin: http://192.168.1.50:8080" -H "Access-Control-Request-Method: GET" -H "Access-Control-Request-Headers: DTGCommKey" http://127.0.0.1:31270/info` | 如果响应里出现 `Access-Control-Allow-Origin`，前端直连才有一线可能；否则固定走 exe 代理（**注意：即使放开也不采用** —— iPad 连不到游戏机的 `127.0.0.1`，见 §4） |
| 8 | 订阅是否可用 | `POST /subscription/DriverAid.PlayerInfo?Subscription=1` 后 `GET /subscription?Subscription=1` | `Entries` 里能读到 `Values`；读不到就退回 `/get` 轮询（逆向规范里这个字段的 schema 就是 `type: 'null'`，所以不通过的概率偏高） |
| 9 | 轮询压力 | 用 **4 Hz** 连续轮询 10 分钟，观察游戏帧率与 API 错误率 | 帧率无明显下降，无 4xx/5xx |
| 10 | 端口是否可改 / 是否监听非回环 | 在另一台机器上 `curl http://<游戏机IP>:31270/info` | 若不通，则确认「必须同机部署」（本方案已按同机设计） |

### 9.3 建议的落地顺序

见 §7.2（已按"开发机 ≠ 游戏机"重排）。简言之：**先做阶段 0–1 的离线部分**（`--tsw-probe`、`TswApiClient`、归一化纯函数、Node 假 API + 单测、`EFB_DEMO=tsw` 前端演示），把需要游戏的第 1–10 项留到能接触游戏时**一次性**做完。

### 9.4 无游戏时的离线验证路径（2026-09-13 补充）

因为开发机与游戏机分离，本轮实现刻意提供了三条不需要游戏的验证通道：

| 通道 | 命令 | 验证什么 |
| --- | --- | --- |
| Node 假 API + 单测 | `npm test` | 客户端的密钥发现、超时、退避、`Result:Error` 容错、哨兵值过滤、端点发现 —— 全部可自动化 |
| 假 API 手动对打 | `node test/tsw-fake-api.js` 后 `XPlaneEfbBridge.exe --tsw-probe` | 把真客户端指向假服务，端到端跑通探测链路 |
| 列车形状的 demo | `npm run demo`（`EFB_DEMO=tsw`） | 前端全部列车逻辑（图标、km/h、功能隐藏、仪表映射） |
| 真游戏一次性核验 | `XPlaneEfbBridge.exe --tsw-probe <输出.txt>` | §9.2 的 1–10 项，一次跑完 |

## 10. 参考来源

1. Dovetail Games 官方论坛《Dovetail Games Train Sim World Api Support》（DTG Alex，2025-10-08，含官方 PDF《TSW External Interface API 1.5 1.pdf》）：<https://forums.dovetailgames.com/threads/train-sim-world-api-support.94488/>
2. ThirdRails《Train Sim World API — Unofficial API Documentation》（非官方，逐端点整理）：<https://thirdrails.org/Downloads/TSW_API_Unofficial_Documentation.pdf>；帮助页：<https://www.beensoft.nl/ThirdRails/Help/TrainSimWorldAPI.html>
3. Dovetail Games 论坛《Api Documentation》（2025-09-19，端口 31270 / `DTGCommKey` / 轮询可行性的讨论）：<https://forums.dovetailgames.com/threads/api-documentation.93807/>
4. `waaghals/tsw-inspector`（逆向 OpenAPI 3.1 规范，MIT）：<https://tsw-inspector.waaghals.dev/openapi.yaml>、<https://github.com/waaghals/tsw-inspector>
5. `GarethLowe/tsw6-realtime-weather`（TSW6 + C# 客户端，TSW6 字段模型的直接证据）：<https://github.com/GarethLowe/tsw6-realtime-weather>
6. `TheJAG/tsw_connect`（TSW6 + Python，10 Hz 订阅轮询、m/s→mph 换算）：<https://github.com/TheJAG/tsw_connect>
7. Steam《Train Sim World® 6》讨论《API Key》（2025-11-13，TSW6 的 `CommAPIKey.txt` 路径与生成条件）：<https://steamcommunity.com/app/3656800/discussions/0/682985658886843643/>
8. Train Sim Community Mod《TSW WebSocket API》（可选替代方案）：<https://www.trainsimcommunity.com/mods/c3-train-sim-world/c109-other/i5723-tsw-web-socket-api>
9. ThirdRails 官网（成品对照）：<https://thirdrails.org/>
