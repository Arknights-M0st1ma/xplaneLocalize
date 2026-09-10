# Navigraph 接入合规说明

最后核对：2026-09-11。此文档依据 Navigraph 官方开发者文档整理；最终许可范围以 Navigraph 对本项目的书面审核结果和届时条款为准。

## 直接结论

拥有 Navigraph 订阅，并不意味着可以直接在自己的网页里调用开发者 API。对本项目而言：

1. **Navigation Data API 技术上包含航点数据，但必须先获开发者批准。** 数据以完整 navdata package 交付，不是一个输入 ident 或经纬度就返回航点的 REST 查询接口。下载后由获批应用解析，例如 DFD 数据库中的 enroute/terminal waypoint 记录。
2. **当前周期导航数据需要有效订阅。** 官方申请页写的是任意有效 Navigraph subscription；API 概览将可返回当前周期的产品称为 Navigation Data 或 Ultimate。程序实际应检查用户订阅中是否含 `fmsdata`。无有效订阅时 packages endpoint 只返回官方定义的旧版默认数据包。
3. **在 OSM 上绘制 Navigraph 航点并非自动获准。** 官方限制明确禁止使用 FMS Data API 生成可被当作航图的图形化或 chart-like rendition。即使只显示点标记，也应在申请时提交截图、显示范围和用途，请 Navigraph 书面确认；未确认前不得启用。
4. **Charts API 与 Navigation Data API 是两套权限。** Charts API 提供机场航图和航路图，需要 Ultimate 订阅及单独批准。官方申请页说明 Charts API 只能授予在飞行模拟器进程内运行的应用；限制页虽提到极少数只能在模拟器局域网中工作的辅助屏幕例外，但本项目是独立 iPad 网页，不能据此自行认定获准。

所以当前版本只保留 Navigraph 官方 Charts 的外部链接，不调用、抓取、代理、缓存或渲染任何 Navigraph API 数据。

## Navigation Data API

获批后用于取得数据包的公开端点是：

```http
GET https://api.navigraph.com/v1/navdata/packages?format=<approved-format>&package_status=current
Authorization: Bearer <access-token>
```

响应包含周期、revision、状态和短时有效的 signed download URL。它不是 waypoint search API。官方 DFD 格式是 SQLite/ASCII 数据集，包含 enroute waypoints、terminal waypoints、navaids、airways、procedures 等记录，应用需自行建立空间索引并只向前端发送当前视口所需对象。

对这个“本地 exe + iPad 网页”架构，若 Navigraph 批准，应采用如下安全边界：

```text
iPad 浏览器 ── Authorization Code + PKCE ── Navigraph Identity
      │ 用户 access token/订阅证明
      ▼
本地 Bridge.exe ── 后端凭据、fmsdata scope ── packages endpoint
      │ 本地解析、按视口裁剪；绝不把 client secret 发给浏览器
      ▼
  iPad 上经批准的显示层
```

- Web/mobile 用户登录：OpenID Connect Authorization Code Flow with PKCE；授权地址为 `https://identity.api.navigraph.com/connect/authorize`，用 `fmsdata` scope，回调 URI 必须与申请时登记值完全一致。
- 网站后端下载数据：Client Credentials Flow，向 `https://identity.api.navigraph.com/connect/token` 提交 `grant_type=client_credentials`、`scope=fmsdata`、Client ID 和 Client Secret。该 flow 只能用于后端，token 约一小时且没有 refresh token。
- 用户订阅校验：用用户 token 调用 `GET https://api.navigraph.com/v1/subscriptions/valid`，确认至少一项 `type` 为 `fmsdata`；网站 flow 还要求定期复核，不能让过期用户继续访问 current 数据。
- 前端不得保存 Client Secret。PKCE 的 state、code verifier、refresh token 和数据缓存策略也必须按审核要求实现。

## Charts API

Charts API 用来取得 Jeppesen 机场航图和 Navigraph 航路图，不应与 Navigation Data API 混用。它要求：

- 用户具有有效 Navigraph Ultimate subscription；
- 开发者另外申请 Charts API 权限及 `charts` scope；
- 应用形态通过 Navigraph 审核。独立实体 iPad EFB/桌面 Charts 类应用属于官方重点限制对象。

官方文档中的典型机场航图端点为 `GET https://api.navigraph.com/v2/charts/{ICAO}?version=STD&rules=IFR`，再使用响应里的受保护图片 URL；航路图则使用受签名 Cookie 保护的 `https://enroute-bitmap.charts.api-v2.navigraph.com/styles/{layer}/{z}/{x}/{y}.png` 瓦片服务及 `tiles` scope。这些地址只是协议说明，不能在未获批准时填入本项目的 XYZ 配置。航图还明确禁止离线保存或缓存。

因此本项目的 `NavigraphExternalUrl` 只是打开 Navigraph 自己的 Charts 页面，不是 API 集成；`AuthorizedChartTileUrl` 也不是规避 Navigraph 授权的入口。

## 如何申请

向 `dev@navigraph.com` 发送申请，至少说明：团队/个人、应用类型、飞行模拟用途、Windows Bridge + 局域网 iPad 架构、申请 Navigation Data API/Charts API 中的哪一种、采用的认证 flow，以及 Authorization Code flow 的准确 Redirect URI。建议同时附上航点叠加效果图，并明确询问“在 OSM 上只绘制 waypoint 点和 ident 是否会被认定为 chart-like rendition”。审核通过后才会获得 Client ID/Secret。

## 当前项目的替代路线

- **优先：读取用户本机 X-Plane 数据。** X-Plane 12 自带 `Resources/default data/earth_fix.dat`；若用户通过合法渠道更新过导航数据库，则优先层位于 `Custom Data/earth_fix.dat`。Laminar 官方提供 XPFIX1200 格式说明。这可以让地图航点与模拟器实际使用的数据一致。实现时应只在本机读取用户已有文件，不随项目重新分发第三方数据，并保留文件内版权信息。
- **FAA CIFP：** 美国范围的免费 28 天周期原始 ARINC 424 数据，需自行解析，覆盖和使用条款需按 FAA 页面核对。
- **OurAirports：** 公共领域的机场和导航台 CSV，适合机场/VOR/NDB 基础叠加，但不是完整全球 IFR waypoint/AIRAC 替代品。
- 无论来源为何，OSM 只作为底图；航点来源、许可、周期和免责声明应单独标示，不能暗示这些点来自 OpenStreetMap。

## 官方资料

- [Navigation Data 简介](https://developers.navigraph.com/docs/navigation-data/introduction)
- [Navigation Data API 与 packages endpoint](https://developers.navigraph.com/docs/navigation-data/api-overview)
- [DFD 数据格式](https://developers.navigraph.com/docs/navigation-data/dfd-data-format)
- [申请开发者访问](https://developers.navigraph.com/docs/request-access)
- [Authorization Code + PKCE](https://developers.navigraph.com/docs/authentication/authorization-code)
- [Client Credentials 与订阅校验](https://developers.navigraph.com/docs/authentication/client-credentials)
- [Navigraph 使用限制](https://developers.navigraph.com/docs/general/restrictions)
- [Charts API 简介](https://developers.navigraph.com/docs/charts/introduction)
- [机场航图端点与限制](https://developers.navigraph.com/docs/charts/airport-charts)
- [航路图瓦片与认证](https://developers.navigraph.com/docs/charts/enroute-charts)
- [X-Plane 11/12 navdata 层级与文件](https://developer.x-plane.com/article/navdata-in-x-plane-11/)
- [FAA CIFP](https://www.faa.gov/air_traffic/flight_info/aeronav/digital_products/cifp/download/)
- [OurAirports 开放数据](https://ourairports.com/data/)
