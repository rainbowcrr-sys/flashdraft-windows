// Shared UI localization. One binary, two complete string sets; the Settings
// page switches between them at runtime. No second build / second package.
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FlashDraft.App;

public static class Loc
{
    public const string English = "en-US";
    public const string Chinese = "zh-CN";

    private static string _lang = English;
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashDraft");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>Raised after a language change so ViewModels can refresh computed text.</summary>
    public static event Action? LanguageChanged;

    public static string Current => _lang;
    public static bool IsChinese => _lang == Chinese;

    // (key, English, Simplified Chinese)
    private static readonly (string Key, string En, string Zh)[] Table =
    [
        ("AppTitle", "FlashDraft", "FlashDraft"),
        ("Subtitle", "LAN and Internet file transfer", "局域网与互联网文件传输"),
        ("PhoneSubtitle", "Secure peer-to-peer transfer", "安全的点对点文件传输"),
        ("Ready", "Ready", "就绪"),
        ("TabTransfer", "Transfer", "传输"),
        ("TabAbout", "About", "关于"),
        ("TabSettings", "Settings", "设置"),
        ("Pairing", "Pairing", "配对"),
        ("MyOfferCode", "My offer code", "我的连接码"),
        ("WatermarkGenerate", "Generate a code first", "请先生成连接码"),
        ("PeerOfferCode", "Peer offer code", "对方连接码"),
        ("GenerateCode", "Generate my code", "生成我的连接码"),
        ("BindPeer", "Bind peer", "绑定对方"),
        ("WanMode", "WAN mode", "公网模式"),
        ("SourceFile", "Source file", "源文件"),
        ("WatermarkSource", "Full path of the file to send", "待发送文件的完整路径"),
        ("DestDir", "Destination directory", "目标目录"),
        ("WatermarkDest", "Leave empty for the default download folder", "留空则使用默认下载目录"),
        ("WaitTransfer", "Wait for transfer", "等待接收"),
        ("SendFile", "Send file", "发送文件"),
        ("Loopback", "Loopback self test", "回环自测"),
        ("Cancel", "Cancel", "取消"),
        ("Progress", "Progress", "进度"),
        ("ReadyNoTransfer", "Ready · no active transfer", "就绪 · 暂无传输"),
        ("AboutTitle", "About", "关于"),
        ("LicenseTitle", "License", "许可全文"),
        ("LicenseAgreement", "License Agreement", "许可协议"),
        ("AcceptLicense", "I have read and accept the license", "我已阅读并接受许可协议"),
        ("DeclineHint", "Decline by closing the window", "不接受请关闭窗口退出"),
        ("DeclineApp", "Decline by closing the app manually", "不接受请手动退出应用"),
        ("Continue", "Continue", "继续"),
        ("TickFirst", "Please tick the box first", "请先勾选"),
        ("BackToTransfer", "Back to Transfer", "返回传输"),
        ("Version", "Version 0.1.2  ·  Apache-2.0", "版本 0.1.2  ·  Apache-2.0"),
        ("SettingsTitle", "Settings", "设置"),
        ("LanguageLabel", "Language", "语言"),
        ("LanguageHint", "Applies to the whole app and is saved on this device.", "作用于整个应用，并保存在本设备。"),
        ("DefaultFromInstall", "Default language follows the installer selection; you can change it here anytime.", "默认语言跟随安装时所选语言，可随时在这里更改。"),
        ("Saved", "Saved. The whole interface has switched.", "已保存，整个界面已切换。"),
        ("LangChinese", "简体中文", "简体中文"),
        ("LangEnglish", "English", "English"),
        ("Step1", "STEP 1", "步骤 1"),
        ("Step2", "STEP 2", "步骤 2"),
        ("Live", "LIVE", "实时"),
        ("Project", "PROJECT", "项目"),
        ("FullText", "FULL TEXT", "全文"),
        ("Step1Of1", "STEP 1 OF 1", "步骤 1 / 1"),
        ("SettingsTag", "PREFERENCES", "偏好"),
    ];

    private static string Lookup(string key)
    {
        string bare = key.StartsWith("Str.", StringComparison.Ordinal) ? key[4..] : key;
        foreach (var row in Table)
            if (row.Key == bare) return IsChinese ? row.Zh : row.En;
        return key;
    }

    public static string T(string key) => Lookup(key);

    public static string Notice() => IsChinese
        ? "界面观感参考 PCL（作者 龙腾猫跃 / Meloong），已获作者明确许可；本作品与 PCL 无官方关联、非官方作品，未使用 PCL 的源代码、XAML、图片、Logo 或字体。"
        : "FlashDraft's UI design is inspired by PCL by 龙腾猫跃 (Meloong), with the author's explicit permission. Independent file-transfer tool, not affiliated with PCL. No PCL source, XAML, images, logos or fonts are used.";

    /// <summary>
    /// Default language priority:
    ///   1. language saved by the user in Settings
    ///   2. locale.ini written by the Windows installer (installer selection)
    ///   3. OS UI culture (Chinese -> Simplified Chinese, otherwise English)
    /// </summary>
    public static string LoadDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("language", out var v))
                {
                    string? s = v.GetString();
                    if (s == Chinese || s == English) return s;
                }
            }
        }
        catch { /* corrupted settings: fall through */ }

        try
        {
            string ini = Path.Combine(AppContext.BaseDirectory, "locale.ini");
            if (File.Exists(ini))
            {
                foreach (string line in File.ReadAllLines(ini))
                {
                    string t = line.Trim();
                    if (t.StartsWith("Language=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = t[9..].Trim();
                        if (v is "zh-CN" or "zh" or "Chinese" or "ChineseSimplified") return Chinese;
                        if (v is "en-US" or "en" or "English") return English;
                    }
                }
            }
        }
        catch { /* no installer file */ }

        try
        {
            if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh") return Chinese;
        }
        catch { /* culture unavailable */ }

        return English;
    }

    public static void Init()
    {
        _lang = LoadDefault();
        ApplyResources();
    }

    public static void Set(string lang)
    {
        _lang = lang == Chinese ? Chinese : English;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, "{\n  \"language\": \"" + _lang + "\"\n}\n");
        }
        catch { /* read-only profile: session-only switch still works */ }
        ApplyResources();
        LanguageChanged?.Invoke();
    }

    private static void ApplyResources()
    {
        try
        {
            var app = global::Avalonia.Application.Current;
            if (app?.Resources is null) return;
            foreach (var row in Table)
                app.Resources["Str." + row.Key] = IsChinese ? row.Zh : row.En;
        }
        catch { /* headless / non-Avalonia host */ }
    }
}
