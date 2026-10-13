# Mineradio 桌面粒子版

Mineradio 的独立修改版本，聚焦桌面粒子视觉、桌面歌词、托盘控制、原生壁纸和歌单导入体验。

## 主要内容

- 粒子视觉与音乐节奏联动
- 桌面歌词与壁纸模式
- 托盘播放控制
- 原生 D3D11 桌面壁纸渲染器
- 平台歌单导入与节拍缓存策略
- <img width="1611" height="948" alt="image" src="https://github.com/user-attachments/assets/908fa3b8-8542-4c6f-bbc7-b49dc59c020c" />


## 本地运行

```powershell
npm install
npm start
```

## 构建原生壁纸组件

```powershell
dotnet publish desktop/native-d3d11-wallpaper/Mineradio.NativeD3D11Wallpaper.csproj -c Release -r win-x64 --self-contained true -o desktop/native-d3d11-wallpaper/publish
dotnet publish desktop/native-wallpaper/Mineradio.NativeWallpaper.csproj -c Release -o desktop/native-wallpaper/publish
```

随后构建 Windows 安装包：

```powershell
npm run build:win
```

## 隐私

请勿提交 Cookie、Token、本地音乐、播放历史、缓存或其他用户数据。详细说明见 [PRIVACY.md](./PRIVACY.md)。

## 许可与致谢

本项目采用 GPL-3.0-only 许可证。原项目、第三方组件及相关设计归属说明见 [LICENSE](./LICENSE) 与 [NOTICE.md](./NOTICE.md)。
