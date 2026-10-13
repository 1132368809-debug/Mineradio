# Mineradio 桌面粒子版

Mineradio 的独立修改版本，聚焦桌面粒子视觉、桌面歌词、托盘控制、原生壁纸和歌单导入体验。

## 普通用户安装（推荐）

只需要下面 3 步：

1. 点击下载：[Mineradio-1.1.1-Setup.exe](https://github.com/1132368809-debug/Mineradio/releases/latest/download/Mineradio-1.1.1-Setup.exe)
2. 下载完成后，双击安装包，按照窗口提示完成安装。
3. 从桌面快捷方式启动 **Mineradio**。

如果 Windows 弹出安全提示，请点击 **更多信息 → 仍要运行**。

> 只需要下载名称以 `Setup.exe` 结尾的安装包。不要下载 `Source code`、`.blockmap` 或 `latest.yml`。

### 更新已有版本

先从系统托盘彻底退出 Mineradio，再运行新版安装包覆盖安装。歌单和本地设置通常会继续保留。

## 功能

- 粒子视觉与音乐节奏联动
- 桌面歌词与壁纸模式
- 托盘播放控制
- 原生 D3D11 桌面壁纸渲染器
- 平台歌单导入与节拍缓存策略

## 界面预览

<img width="1611" height="948" alt="Mineradio 桌面粒子版界面" src="https://github.com/user-attachments/assets/908fa3b8-8542-4c6f-bbc7-b49dc59c020c" />

## 开发者从源码运行

```powershell
npm install
npm start
```

## 开发者构建安装包

先构建原生壁纸组件：

```powershell
dotnet publish desktop/native-d3d11-wallpaper/Mineradio.NativeD3D11Wallpaper.csproj -c Release -r win-x64 --self-contained true -o desktop/native-d3d11-wallpaper/publish
dotnet publish desktop/native-wallpaper/Mineradio.NativeWallpaper.csproj -c Release -o desktop/native-wallpaper/publish
```

然后构建 Windows 安装包：

```powershell
npm run build:win
```

## 隐私

请勿提交 Cookie、Token、本地音乐、播放历史、缓存或其他用户数据。详细说明见 [PRIVACY.md](./PRIVACY.md)。

## 许可与致谢

本项目采用 GPL-3.0-only 许可证。原项目、第三方组件及相关设计归属说明见 [LICENSE](./LICENSE) 与 [NOTICE.md](./NOTICE.md)。
