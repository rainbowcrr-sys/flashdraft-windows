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

## How to transfer a file / 如何传输文件

1. Both peers click "Generate my code" and exchange the two `flashdraft://` codes out of band (QR, paste or chat). Each code contains an ephemeral public key, LAN candidates, a STUN reflexive address, a one-time token and a five-minute expiry.
   两端各自点击「生成我的码」，并通过带外渠道（二维码、粘贴或聊天）互相交换 `flashdraft://` 连接码。每个码含临时公钥、内网候选地址、STUN 反射地址、一次性 token 与五分钟过期时间。
2. Paste the peer's code into "Peer offer code" and click "Bind peer" on both sides. This derives the same session id and directional keys from the two ephemeral key pairs.
   把对端的码粘进「对端连接码」，两端各自点击「绑定对端」，由两对临时密钥派生出相同的会话 ID 与双向密钥。
3. On the receiving peer set a destination directory and click "Wait for transfer". On the sending peer set a source file and click "Send file".
   接收端设置目标目录后点「等待传输」，发送端设置源文件后点「发送文件」。
4. For a LAN leave "WAN mode" off (TCP). For the public Internet switch "WAN mode" on at both ends: the sender punches a UDP hole toward the reflexive address in the peer's code, then encrypted 1 KiB chunks flow with ACK and retransmission. There is no signaling server and no automatic WAN discovery; symmetric NAT may require a fresh code or manual port mapping.
   局域网请关闭「公网模式」（走 TCP）。跨公网时两端都勾选「公网模式」：发送端向对端码里的反射地址发起 UDP 打洞，随后以 1 KiB 加密分块配合 ACK 与重传传输。本项目没有信令服务器，公网无法自动发现对端；对称 NAT 可能需要重新交换连接码或手动端口映射。
5. "Loopback self test" runs two in-process peers on one device and really transfers a generated file over loopback TCP. It needs no second device and no network.
   「单机自测」会在同一台设备的一个进程里起两个对等端，通过本机回环 TCP 真实传输一个生成的文件，不需要第二台设备，也不需要网络。

See the flashdraft-core README for the full protocol description. 完整协议说明见 flashdraft-core 仓库 README。

## PCL notice / PCL 声明

FlashDraft's user interface design is inspired by Plain Craft Launcher (PCL) by 龙腾猫跃 (Meloong). The author has explicitly granted permission for this use. This project contains no PCL source code, XAML, images, logos, or fonts. FlashDraft is an independent file-transfer tool and is not affiliated with, endorsed by, or officially related to PCL or its author. PCL and all related rights remain with the original author. See https://github.com/Meloong-Git/PCL/blob/main/LICENCE.

界面观感参考 PCL（作者 龙腾猫跃 / Meloong），已获作者明确许可；本作品与 PCL 无官方关联，非官方作品。本项目不包含 PCL 的源代码、XAML、图片、Logo 或字体；PCL 及相关权利归原作者所有。参见 https://github.com/Meloong-Git/PCL/blob/main/LICENCE。

## License / 许可

Apache-2.0 for FlashDraft's own code. See `LICENSE`. Shared architecture and contribution rules live in the flashdraft-core repository.
