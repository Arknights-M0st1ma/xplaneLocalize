# Windows 单文件桥接器

该 WinForms 托盘程序内置 Kestrel HTTP/WebSocket 服务、X-Plane UDP 解析器、可视化设置窗口和完整前端资源。运行时只需要一个 exe。

## 发布

```powershell
dotnet publish .\windows-bridge\XPlaneEfbBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\windows-bridge\dist
```

产物：

```text
windows-bridge\dist\XPlaneEfbBridge.exe
```

`SelfContained` 让目标电脑无需预装 .NET；`IncludeNativeLibrariesForSelfExtract` 将 WinForms 原生依赖纳入同一个 exe；网页通过 EmbeddedResource 内嵌。

exe 图标来自 `app.ico`（csproj 里的 `ApplicationIcon`），托盘图标与设置窗口图标运行时从 exe 自身提取，因此资源管理器、任务栏、托盘一致。图标与 `public/assets/icon.svg`（浏览器标签页图标）保持同一套几何与配色；要重新生成：

```powershell
python .\windows-bridge\tools\make-icon.py   # 需要 Pillow，生成 app.ico（16/24/32/48/64/128/256）
```

首次运行配置位于：

```text
%LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json
```

同一目录下还有 `flightplan-history.json`（归档的飞行计划 + OFP）、`apt-index.json`（apt.dat 索引）
和 `track.json`（**桥接器记录的航迹**）。航迹由电脑端采集，iPad 只通过 `/api/track` 读取，
所以切走 Safari、锁屏都不会再丢中间那段；`track.json` 每 20 秒与退出时各写一次，
点 EFB 里的“清轨迹”会同时清掉这份文件。

## 设置窗口

托盘菜单 → “设置…”：六个分组（连接 / 数据源 / 地图 / SimBrief / 天气 / 高级），保存时先备份再原子替换，端口类改动与数据源切换会询问是否立即重启服务。

配置读写集中在 `ConfigStore.cs`：读取时保留未知键，写坏的文件只备份不回写；写入前校验可解析，写入后回读确认，期间若文件被其它程序改动会拒绝覆盖。

## 代码结构

```text
Program.cs        入口、托盘、UDP 服务、Kestrel、SimBrief 客户端、数据源选择
TswApiClient.cs   TSW6 本机 HTTP API 客户端（密钥发现、两级探测、Result 判定、容错解析）
TswTelemetrySource.cs  列车遥测轮询循环；TswMapper 为可单测的纯函数字段归一化
TswProbeDump.cs   --tsw-probe 诊断报告（端点清单、原始响应、可用字段）
TrackStore.cs     航迹记录器（0.1 NM / 5 秒采样、增量读取、原子落盘）
ConfigStore.cs    配置读取 / 迁移 / 原子写入 / 备份 / 冲突检测
FlightPlanHistory.cs  本地航路归档（历史计划 + 完整 OFP 文本）
WeatherTiles.cs   OpenWeatherMap 瓦片代理（白名单 / 坐标校验 / 缓存 / 限流）与出网 HttpClient
AptDat.cs         本机 apt.dat 解析与索引（跑道/铺面/标线/标牌/机位/滑行路线，按需解析单个机场）
SecretProtection.cs  代理密码的 DPAPI 加解密（crypt32 P/Invoke，无额外依赖）与凭据脱敏
app.ico              exe / 窗体 / 托盘图标（tools/make-icon.py 可重新生成）
SettingsForm.cs   设置窗口（WinForms，纯代码布局，按 DPI 缩放）
FolderPicker.cs   内置文件夹/文件选择器（不经过 Shell 扩展，避免第三方扩展导致崩溃）
DevTools.cs       开机自启注册表、隐藏的自检与界面截图开关
```

## 隐藏开关（用于验证，不属于用户流程）

```powershell
# 配置存储自检：读旧配置、未知键保留、备份、原子写、损坏文件、冲突拒绝，以及 TSW 字段
# 归一化、哨兵过滤、ClearSnapshot 回归、密钥不出现在诊断文本里
# （设置 EFB_TSW_FAKE_BASE 可额外对 test/tsw-fake-api.js 做端到端检查）
XPlaneEfbBridge.exe --config-selftest <临时目录>
# 自检结果写到 %TEMP%\efb-config-selftest.log，退出码 0 = 全部通过

# Train Sim World 6 API 探测：可达性、密钥、/list、关键节点端点清单、候选端点原始响应
XPlaneEfbBridge.exe --tsw-probe <输出.txt> [配置文件路径]

# 把设置窗口渲染成 PNG（不需要显示器，用于检查布局）
# 页签序号 0–5：连接 / 数据源 / 地图 / SimBrief / 天气 / 高级
XPlaneEfbBridge.exe --settings-preview <输出.png> [页签序号 0-5]

# 只读取/修复配置文件，不启动服务
XPlaneEfbBridge.exe --repair-config <配置文件路径>

# 不显示托盘、直接以控制台方式跑服务（调试用）
XPlaneEfbBridge.exe --headless <配置文件路径>

# 排查 SimBrief 响应格式：按本机配置请求一次，把原始响应写到指定文件，并生成 <文件>.shape.txt 结构摘要
XPlaneEfbBridge.exe --dump-ofp <输出.json> [配置文件路径]

# 用现成的响应文件离线验证解析结果（航点数、起降、距离、首个/末个航点）
XPlaneEfbBridge.exe --parse-ofp <响应.json> <结果.txt>

# 天气代理诊断：把上游换成本地假瓦片服务（正常使用不需要设置）
$env:EFB_OWM_BASE = 'http://127.0.0.1:8099/map'

# TSW 客户端诊断：把 API 基址换成任意地址（指向 test/tsw-fake-api.js 或自建转发）
$env:EFB_TSW_BASE = 'http://127.0.0.1:31270'
```

注意：`--config-selftest` 里有一项验证 DPAPI 加解密；在受限沙箱/禁用凭据服务的环境下会失败（正常登录的 Windows 上通过）。

最终用户说明、X-Plane 设置、防火墙和验收步骤见项目根目录 `README.md`。

OpenStreetMap 是默认主底图。也可填写 `CustomBaseMapName`、`CustomBaseMapUrl` 和 `CustomBaseMapAttribution` 接入已获授权的 XYZ 服务。损坏或拼接了多个根对象的配置会先备份，再自动修复为单一合法 JSON。
