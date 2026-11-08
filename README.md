# FlashDraft for Windows

Windows client for FlashDraft - LAN and Internet file transfer tool.

FlashDraft 的 Windows 客户端（局域网与互联网文件快传工具）。

## Requirements / 运行环境

- Windows 10 (build 19041) or Windows 11
- Latest stable .NET SDK for development

## Build / 构建

```powershell
dotnet restore FlashDraft.Windows.sln
dotnet build FlashDraft.Windows.sln -c Release
dotnet run --project src/FlashDraft.Desktop/FlashDraft.Desktop.csproj
```

The Avalonia UI sources live in `src/FlashDraft.App/`. A vendored copy of `FlashDraft.Core` is kept under `vendor/` so the first build runs without a published package. Replace the `ProjectReference` with `PackageReference Include="FlashDraft.Core" Version="0.1.0"` after the first NuGet release.

Avalonia 界面源码位于 `src/FlashDraft.App/`。`vendor/` 下保留一份 `FlashDraft.Core` 副本，使首次构建无需已发布的包；首个 NuGet 包发布后，请把 ProjectReference 换成 `PackageReference Include="FlashDraft.Core" Version="0.1.0"`。

## Distribution / 分发

Installers are unsigned at zero cost. Every release ships a SHA-256 checksum. Windows SmartScreen may warn: choose More info, then Run anyway, or verify the checksum first. See the release notes for the full bypass steps.

安装包暂不签名，随版本提供 SHA-256 校验和。若出现 SmartScreen 提示，可先校验和，再选择「更多信息」→「仍要运行」。

## PCL notice / PCL 声明

FlashDraft's user interface design is inspired by Plain Craft Launcher (PCL) by 龙腾猫跃 (Meloong-Git/PCL). This project contains no PCL source code, images, logos, or fonts. FlashDraft is an independent file-transfer tool and is not affiliated with, endorsed by, or officially related to PCL or its author. PCL and all related rights remain with the original author. See https://github.com/Meloong-Git/PCL/blob/main/LICENCE.

FlashDraft 的界面设计受龙腾猫跃的 Plain Craft Launcher (PCL) 启发。本项目不包含 PCL 的源代码、图片、Logo 或字体。FlashDraft 是独立的文件传输工具，与 PCL 及其作者无关联、非官方作品、未获其背书。PCL 及相关权利归原作者所有。参见 https://github.com/Meloong-Git/PCL/blob/main/LICENCE。

## License / 许可

Apache-2.0 for FlashDraft's own code. See `LICENSE`. Shared architecture and contribution rules live in the flashdraft-core repository.
