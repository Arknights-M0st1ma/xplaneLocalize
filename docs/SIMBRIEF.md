# SimBrief 航路接入调研与实现说明

核对时间：2026-09-11。资料来自 Navigraph 官方开发者文档与官方 API 支持论坛的置顶帖，链接见文末。

## 结论

**可行，而且不需要 API Key。** SimBrief 提供两个不同的接口，容易混淆：

| 接口 | 用途 | 凭据 | 本项目是否使用 |
| --- | --- | --- | --- |
| Dispatch API（表单 + `simbrief.apiv1.js/php`） | 让第三方网站**生成**新的飞行计划 | 必须申请 API Key | 不使用 |
| OFP Fetcher（`xml.fetcher.php`） | 读取某位飞行员**最新**的飞行计划 | 只用 Pilot ID 或 Navigraph 用户名 | 使用 |

因此本项目只做“读”，不做“生成”，也就不需要向 SimBrief 申请开发者凭据。

## 端点与认证

```http
GET https://www.simbrief.com/api/xml.fetcher.php?userid={pilot_id}&json=v2
GET https://www.simbrief.com/api/xml.fetcher.php?username={navigraph_alias}&json=v2
```

- `userid`：SimBrief Pilot ID，1–7 位数字；官方建议只有在配置流程只能输入数字（例如在 FMS 里配置）时才用。
- `username`：Navigraph 用户名 / 别名，官方更推荐。
- 无 `Authorization` 头，无 API Key，无 OAuth。
- 返回：成功为 HTTP 200 + 计划数据；无效用户或错误为 HTTP 400 + 一小段错误信息。

错误响应实测（2026-09-11，用无效 Pilot ID 探测，未读取任何他人计划）：

```json
{"fetch":{"userid":"999999999","static_id":"","status":"Error: Unknown UserID","time":"0.0001"}}
```

所以判定成功的可靠依据是 HTTP 状态码 + `fetch.status`，而不是“有 JSON 就当成成功”。

### 格式版本

- 默认返回 XML；加 `&json=1` 返回 JSON，但官方说明该版本“有一些格式问题”，仅为兼容保留。
- 官方建议新实现使用 `&json=v2`。本项目默认使用 `json=v2`，解析器同时容忍两种格式里“单个对象 vs 数组”的差异。

## 免费 / 订阅限制

- 读取自己的 OFP 不需要付费订阅，只需要一个 SimBrief（或 Navigraph）账号，因为计划的生成与维护在 SimBrief 一侧。
- Navigraph 订阅影响的是导航数据周期（AIRAC）、航图等增值内容，会体现在计划本身的航路与航点里，不改变这个端点的可用性。
- Charts API、Navigation Data API 属于另一套需要单独审核的开发者接口，与本端点无关，本项目不涉及。

## 调用频率与合规要求

官方置顶帖的原话（Usage Restrictions）：

> This endpoint should only be called in response to a user action, for example, whenever your user clicks an “Import Flight Plan” button. You should not repeatedly poll this endpoint in an effort to detect if the user generates a new flight. Calls to this endpoint are monitored, and excessive calls may result in your app being automatically banned by the server’s firewall.

本项目的实现方式正是按这条规则设计的：

1. **只在用户点击“获取/刷新航路”时调用一次**，没有任何定时器或后台刷新。
2. 结果缓存在运行桥接器的电脑上（exe：`%LOCALAPPDATA%\XPlaneEfbBridge\flightplan-cache.json`；Node 调试模式：进程内存），重启后不需要再次请求。
3. 两次请求之间强制至少间隔 30 秒，防止误触连点。
4. 只把数据用于同一位用户自己的移动地图显示，不再分发、不代理给第三方。

## 数据格式（本项目实际使用的字段）

OFP JSON 顶层是一个大对象，常用子对象：

| 路径 | 用途 |
| --- | --- |
| `general.route` | 航路字符串（含 SID/STAR 与航路名） |
| `general.aircraft_icao` / `aircraft.icaocode` | 机型 |
| `general.route_distance` / `general.gc_distance` | 航路距离 / 大圆距离（NM） |
| `general.initial_altitude` / `general.cruise_altitude` | 巡航高度（英尺） |
| `times.est_time_enroute` | 预计航时（秒） |
| `origin` / `destination` | 起降机场：`icao_code`、`iata_code`、`name`、`plan_rwy`、`pos_lat`、`pos_long` |
| `alternate`（可能是一个对象或数组）、`takeoff_altn` | 备降机场，字段同上 |
| `navlog` | 航路点序列。**`json=v2` 是扁平数组**（实测 2026-09-11，一份 EDDF–VHHH 计划返回 124 个元素）；旧格式/XML 变体是 `navlog.fix`（数组或单个对象）。每项含 `ident`、`name`、`type`（`wpt`/`vor`/`ndb`/`apt`…）、`via_airway`、`altitude_feet`、`stage`（`CLB`/`CRZ`/`DSC`）、`is_sid_star`、`pos_lat`、`pos_long`，另外还有大量本项目不使用的气象/燃油字段 |
| `alternate_navlog` | 每个备降场各自的航路点数组（数组的数组），本项目暂不使用 |

坐标在 JSON 里是十进制度（例如 `"31.143400"`）；XML 变体也可能出现 `N51.470000` 这类半球前缀写法，因此解析器两种都接受。SimBrief 对“没有位置的机场”会给出 `0/0`，本项目把 `0/0` 当作缺失而不是真坐标，避免整条航路被拉到几内亚湾。

**`navlog` 有三种形态，解析必须都容忍**（否则会误判为“没有航点明细”）：

1. **扁平数组**（`json=v2` 的实际情况）：`"navlog": [ {…航点…}, … ]`；
2. **嵌套对象**（旧格式/XML）：`"navlog": { "fix": [ … ] }`，并且 `fix` 也可能是单个对象；
3. **空字符串**：飞行员关闭了 Detailed Navlog，`"navlog": ""`。此时**不能直接对它取子键**，.NET 会抛 `The node must be of type 'JsonObject'`——必须先判断节点类型。

形态 3 下接口仍会返回 `general.route`（航路文字）与起降/备降机场，本项目会照常显示机场与直连线，并提示用户去 SimBrief 打开 Detailed Navlog 才能画出完整航点。

可靠性设计：所有字段都做了“取不到就跳过”的处理（含上面的空字符串情况），某个航点缺坐标只影响这个点，不会让整条航路或整个网页失败；解析失败时给出的提示里会带上响应顶层字段名与节点类型，便于定位 SimBrief 的格式变化，而不是抛出一句 .NET 异常。

## 本项目已实现的内容

```text
SimBrief (www.simbrief.com)
   ▲ 只有点击“获取/刷新航路”时才请求一次（HTTPS，无密钥）
   │
XPlaneEfbBridge.exe  ──解析成精简航路 JSON──►  本地缓存（磁盘）
   │ HTTP /api/flightplan（同源，局域网内）
   ▼
iPad 网页  ──►  OSM 底图 + 飞机位置 + 航路虚线 / 航点 / 起降备降标签
```

- 配置项：`SimbriefUser`（Pilot ID 或 Navigraph 用户名）、`SimbriefApiUrl`（可留空，仅在需要自建代理时使用）。Pilot ID 只留在电脑本地配置里，不下发到网页。
- 接口：`GET /api/flightplan` 返回当前缓存（不会触发对 SimBrief 的请求）；`GET /api/flightplan?refresh=1` 才会去拉取。
- 前端：航路以紫色虚线绘制，航点是可点按的圆点（显示航点、航路、高度），起降/备降机场带常驻标签；标签在航向向上时会被反向旋转保持正向可读。
- 可以随时用抽屉里的“SimBrief 航路”开关叠加或隐藏，与飞机位置、航迹互不干扰。

### 与飞机位置叠加的表现

- 航路、航点、机场属于同一层组，跟随地图一起平移和旋转；飞机图标与实时航迹画在它们之上，不会被遮挡。
- 航点用画布渲染（`preferCanvas`），上百个航点也不会明显影响帧率。
- 只有起降/备降机场有常驻文字标签，航点标识按需点按显示，避免上百个标签拖慢 iPad。

## 局限与替代方案

### 只能取到“最新一份”计划（实测结论）

官方抓取接口的设计就是“某位飞行员**最新**的飞行计划”，没有历史列表。2026-09-11 用同一账号实测：

| 请求 | 结果 | 说明 |
| --- | --- | --- |
| `?userid=…&json=v2` | HTTP 200，完整计划 | 基线 |
| `&request_id=1` | HTTP 200，**响应长度与基线完全相同** | 参数被忽略 |
| `&request_id=<当前计划的 request_id>` | HTTP 200，与基线相同 | 参数被忽略 |
| `&sequence_id=zzz` | HTTP 200，与基线相同 | 参数被忽略 |
| `&static_id=none` | **HTTP 400**“Error: No flight plan on file for the specified Static ID” | `static_id` 是唯一被真正识别的选择参数 |

`static_id` 只对“通过 Dispatch API 生成的计划”有效（生成时可以指定并长期引用），而 Dispatch API 需要申请 API Key；网站上手点生成的计划该字段为空，因此无法用任何公开参数回到上一份计划。

**本项目的做法：本地归档。** 每次成功拉取的计划都会存进 `%LOCALAPPDATA%\XPlaneEfbBridge\flightplan-history.json`（最多 12 份，按账号哈希隔离），iPad 的“历史计划（本地归档）”下拉列表可以随时切回，切换不联网、不触发任何 SimBrief 请求。代价是：**只有曾经被本程序拉取过的计划才能被归档**——第一次拉取之前就已经被覆盖的旧计划，接口和第三方工具都拿不到。

### 完整 OFP 文本从哪来

响应里的 `text.plan_html` 就是完整 OFP（实测一份洲际计划约 390 KB，形如 `<div style="..."><pre>…</pre></div>`，内部只有排版标签与少量链接、没有脚本），另有纯文本的 `text.tlr_section`（起降性能报告）。本项目把它转成纯文本后保存在本地归档里，前端用 `<pre>` 显示——既保留航路表的等宽排版，又避免把第三方 HTML 注入到 EFB 页面。

- **需要用户自己先有 SimBrief 计划**：如果账号里没有生成过计划，接口会返回错误；本项目不会替你生成计划（那属于 Dispatch API，需要 API Key 并会创建账号上的新记录）。
- **计划更新不是实时的**：SimBrief 没有推送，只能由用户点击刷新；这是官方限制决定的，不做轮询。
- **网络**：拉取由电脑发起，要求电脑能访问 `www.simbrief.com`。受限网络可用 `SimbriefApiUrl` 指向自建代理。
- **完全离线的替代方案**（不需要 SimBrief、也不需要联网）：读取用户本机 X-Plane 12 的 `Resources/default data/earth_fix.dat` 与 `Custom Data/earth_fix.dat`（XPFIX1200 格式），或使用公开的 FAA CIFP / OurAirports 数据。这些只提供航点/机场点，不含完整飞行计划；本项目目前没有内置，后续如需要可以再加一个“本地航点层”。任何数据源都应与 OSM 底图分开标注来源与有效期。

## 已知的验证边界

- 已实测：端点存在、无需密钥、无效 Pilot ID 返回 HTTP 400 及上述错误结构、`json=1`/`json=v2` 参数、官方对轮询的禁止性说明、官方文档的 URL 形式。
- 未实测：一份真实账号的完整成功响应（为避免读取他人飞行计划，没有用随机 Pilot ID 拉取）。解析器因此写成多键名、单对象/数组都兼容的形式，并配套了单元测试与示例 OFP（`test/fixtures/simbrief-ofp.json`）。填入你自己的 Pilot ID 后，抽屉里会出现起降、备降、航路与航点数，即可确认字段映射正确。

## 官方资料

- [Fetching a User's Latest OFP Data](https://developers.navigraph.com/docs/simbrief/fetching-ofp-data)
- [Using the API（Dispatch API 与 API Key）](https://developers.navigraph.com/docs/simbrief/using-the-api)
- [API 支持论坛置顶：Fetching OFP data（含 Usage Restrictions）](https://forum.navigraph.com/t/fetching-a-users-latest-ofp-data/5297)
- [API 支持论坛置顶：The SimBrief API](https://forum.navigraph.com/t/the-simbrief-api/5298)
- [SimBrief 支持页（开发者联系与 API Key 申请）](https://www.simbrief.com/home/?page=support)
