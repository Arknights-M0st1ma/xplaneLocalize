# v0.4.2 更新说明

发布日期：2026-09-24 · 适用平台：Windows x64（单文件 exe，目标电脑无需安装 .NET）

本次是**缺陷修复版本**：解决「桌面端显示读不到列车坐标，但设置窗口的“测试连接”却能读到」的问题。没有新增功能，也没有改动前端资源（网页不需要清缓存）。

---

## 1. 位置探测失败一次就永久放弃

### 表现

| 现象 | 说明 |
| --- | --- |
| 托盘/诊断显示读不到坐标，iPad 一直「等待 TSW 数据」 | 但设置窗口点「测试连接」能正常读到坐标与车次 |
| 重启服务（托盘 → 重新加载配置）后偶发又能用 | 因为重启会重新做一次探测 |

### 原因

位置端点的探测**只做一次**。而桥接器通常比游戏先启动，或者游戏还停在主菜单——这两种情况下 API 里本来就没有坐标。那一次探测失败后，旧代码把它当成永久失败：后续每个轮询周期直接返回，再也不会重新探测，直到服务重启。

设置窗口的「测试连接」是**即时执行**的，所以你点它的时候游戏已经进了线路、自然读得到。这个差异让问题看起来像「字段名写错了」。

### 修复

- 探测改为按冷却时间自动重试（15 秒一次），游戏启动或进入线路后**不需要重启程序**。
- 「API 正常但暂时没有坐标」（主菜单 / 加载中）与「完全连不上」分开处理：前者不进入失败退避，否则进入线路后还要等半分钟才出点；后者保留指数退避，避免白白打接口。
- 连不上时的提示改为可执行的说明：「连不上 TSW6 API（地址）：游戏没在运行，或启动项里没有 `-HTTPAPI`。游戏启动后会自动重试，不需要重启本程序。」

## 2. 位置可能只在 `/subscription` 里

### 原因

代码原先只在 `/get/DriverAid.PlayerInfo`、`/get/DriverAid.Data`、`/get/CurrentDrivableActor.LatLon` 这些候选端点上找 `geoLocation`。但两个公开的 TSW6 客户端（`GarethLowe/tsw6-realtime-weather`、`TheJAG/tsw_connect`）**都只用订阅取位置**，`/get` 这条路只有 TSW5 时代的非官方文档背书。部分 TSW6 版本的 `/get` 响应确实不带坐标。

### 修复

- 新增订阅回退：`/get` 的候选端点都拿不到坐标时，自动 `POST /subscription/{node}.{endpoint}?Subscription=N`，再 `GET /subscription?Subscription=N`。
- 订阅信封 `{RequestedSubscriptionID, Entries:[{Values:{…}}]}` 由 `TswMapper.SubscriptionValues` 解析，取**第一个带坐标的条目**（不是想当然取 `Entries[0]`）。
- 深度搜索现在会进入数组，`FindDeep` 的深度上限从 3 提到 5。
- 订阅 id 随机生成，避免与 ThirdRails、控制器软件等同时运行的程序抢同一个 id；服务停止时尽力注销订阅。
- 状态里会标出实际用的是哪条路：`/api/status` 的 `tsw.endpoints.position` 显示 `DriverAid.PlayerInfo（订阅）`，并新增 `positionMode`（`get` / `subscription`）。

## 3. 回环请求不该走系统代理

### 原因

TSW 客户端原先用默认 `HttpClient`，会使用系统代理。当代理把 `127.0.0.1` 黑洞化时，每次请求都要等满 1 秒超时；4 个候选端点串起来就是数秒，表现与「游戏没有坐标」完全一致，还会让探测看起来像彻底失败。

### 修复

- `UseProxy = false`，连接超时压到 1 秒。

## 4. 诊断能力

- **设置窗口 → 测试连接**：位置探测失败时会列出每个候选端点**实际返回的字段名**（只列名字，不含值，也不含密钥），例如
  `DriverAid.PlayerInfo → HTTP 200，字段：currentTile、currentServiceName、playerProfileName`。
  有这一行就能判断是端点改名还是字段改名，不必再猜。
- **`--tsw-probe`**：报告同样覆盖订阅，结论会写成 `可用的位置端点：DriverAid.PlayerInfo（订阅）→ 51.47040, -0.45930`；没有位置时也会明确说明「已试 /get 与 /subscription」。

## 5. 其他

- `test/tsw-fake-api.js` 现在实现了订阅路由（注册 / 读取 / 注销、未知 id 仍然 400），并新增 `subscription-only` 场景：`/get` 正常返回但**不含坐标**，位置只在订阅里——就是本次问题的测试替身。命令行可指定场景：`node test/tsw-fake-api.js 31270 subscription-only`。
- `docs/TSW6-TELEMETRY.md` 新增「实测修正记录」，把上述三条与原先基于公开文档的推断的出入写清楚。
- 版本号升到 `0.4.2`（`package.json` 与 `.csproj`）。

## 测试与验证

| 项 | 结果 |
| --- | --- |
| 编译 | `dotnet build -c Release` **0 警告 0 错误** |
| C# 自检（离线） | **150 项，149 通过**；唯一失败是「代理密码加密后不可读」，这是自检文档里注明的构建环境没有 DPAPI，与代码无关 |
| C# 自检（起 `test/tsw-fake-api.js`） | **165 项，164 通过**，同上一条的 DPAPI 例外 |
| 新增自检 | 订阅信封里取出带坐标的那一条 / 深度搜索进入数组 / 订阅信封整体没有坐标时返回空 / 字段诊断只列名字、假 API 的订阅注册·读取·注销 / **轮询循环端到端出帧** |
| 真 exe 端到端①：位置只在订阅里 | 假 API 用 `subscription-only` 场景：**1 秒**出帧，`endpoints.position = DriverAid.PlayerInfo（订阅）`、`positionMode = subscription` |
| 真 exe 端到端②：先启动程序、游戏后启动 | 程序先起（此时连不上）→ 5 秒后游戏出现 → **1 秒**恢复推送，期间提示为「连不上 TSW6 API…游戏启动后会自动重试」 |
| `--tsw-probe` 实测 | 订阅场景下结论为 `可用的位置端点：DriverAid.PlayerInfo（订阅）→ 51.47040, -0.45930`，退出码 0 |
| Node 测试 | `npm test` **82 项全通过** |

> 说明：以上都是与自动化测试替身（假 API）的验证。真实游戏里的端点名与字段名仍以 `--tsw-probe` 的报告为准——这一版的价值在于：无论位置在 `/get` 还是 `/subscription`，无论程序先启动还是游戏先启动，它都能自己走到正确的路上。

## 升级方式

替换 exe 即可，配置、计划历史与航迹都在 `%LOCALAPPDATA%\XPlaneEfbBridge\`。
数据源仍按上一版的选择生效；如果你还没按 v0.4.1 的说明重新选过数据源，请在托盘 → 设置… 里重选一次「Train Sim World 6」并保存。
本次没有改动前端资源，iPad 刷新页面即可。

## 已知限制

- 仅用于模拟飞行 / 模拟驾驶娱乐，**不得用于真实导航或真实铁路调度**。
- exe 未做代码签名，Windows SmartScreen 会提示。
- 服务监听 `0.0.0.0` 且没有登录鉴权，只适合可信的家庭/训练局域网，不要做端口转发。
- TSW6 需要 PC 版、Steam 启动项加 `-HTTPAPI`，并且游戏要与 exe 在同一台电脑上运行（API 只监听回环地址）。
