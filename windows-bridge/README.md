# Windows 单文件桥接器

该 WinForms 托盘程序内置 Kestrel HTTP/WebSocket 服务、X-Plane UDP 解析器和完整前端资源。运行时只需要一个 exe。

## 发布

```powershell
dotnet publish .\windows-bridge\XPlaneEfbBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\windows-bridge\dist
```

产物：

```text
windows-bridge\dist\XPlaneEfbBridge.exe
```

`SelfContained` 让目标电脑无需预装 .NET；`IncludeNativeLibrariesForSelfExtract` 将 WinForms 原生依赖纳入同一个 exe；网页通过 EmbeddedResource 内嵌。

首次运行配置位于：

```text
%LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json
```

最终用户说明、X-Plane 设置、防火墙和验收步骤见项目根目录 `README.md`。

OpenStreetMap 是默认主底图。也可填写 `CustomBaseMapName`、`CustomBaseMapUrl` 和 `CustomBaseMapAttribution` 接入已获授权的 XYZ 服务。损坏或拼接了多个根对象的配置会先备份，再自动修复为单一合法 JSON。
