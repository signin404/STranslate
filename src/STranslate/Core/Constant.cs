using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace STranslate.Core;

public class Constant
{
    public const string AppName = "STranslate";
    public const string Plugins = "Plugins";
    public const string PortableFolderName = "PortableConfig";
    public const string Cache = "Cache";
    public const string Settings = "Settings";
    public const string Logs = "Logs";
    public const string PluginMetaFileName = "plugin.json";
    public const string TmpPluginFolderName = "STranslateTmpPlugins";
    public const string TmpConfigFolderName = "STranslateTmpConfig";
    public const string SystemLanguageCode = "system";
    public const string HttpClientName = "DefaultClient";
    public const string HostExeName = "z_stranslate_host.exe";
    public const string TaskName = "STranslateSkipUAC";
    public const string EmptyHotkey = "None";
    public const string PluginFileExtension = ".spkg";
    public const string NeedDelete = "NeedDelete.txt";
    public const string NeedUpgrade = "_NeedUpgrade";
    public const string InfoFileName = ".INFO";
    public const string BackupFileName = ".BACKUP";

    public const string GitHub = "https://github.com/ZGGSONG/STranslate";
    public const string Website = "https://stranslate.zggsong.com";
    public const string Sponsor = "https://github.com/ZGGSONG/STranslate/tree/2.0?tab=readme-ov-file#donations";
    public const string Group = "https://t.me/+lTVZGHgZtp0zMTVl";
    public const string Report = "https://github.com/zggsong/stranslate/issues/new/choose";
    public const string Dev = "Dev";
    public static readonly string Version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location.NonNull()).ProductVersion.NonNull();

    /// <summary>
    ///     用户软件根目录
    /// </summary>
    /// <remarks>
    ///     <see cref="Environment.CurrentDirectory" />
    ///     * 使用批处理时获取路径为批处理文件所在目录
    /// </remarks>
    public static readonly string ProgramDirectory = AppDomain.CurrentDomain.BaseDirectory;

    public static readonly string LogDirectory = Path.Combine(ProgramDirectory, Logs);
    public static readonly string PreinstalledDirectory = Path.Combine(ProgramDirectory, Plugins);

    public static readonly List<string> PrePluginIDs =
    [
        "3410e7de989340938301abd6fcf8cc4b", //WeChatOCR
        "474b5fe844d9455ba0c59f75c1424f0d", //BigModel
        "4ed3beaab50842e6851a2e4bdbbeccae", //BingDict
        "0b5d84917783415d865032f1d6e2877f", //GoogleBuiltIn
        "0f6892a390a543709926092aba510273", //KingSoftDict
        "9e44abfa040e443c9ab48205683082f4", //MTranServer
        "76b14a8d707041c891a2dcd2f74be9c1", //OpenAI
        "2cc83275790ba8ce96b31c4fe0655743", //TransmartBuiltIn
        "7a3ab25875294602b3afc4ae15fec627", //MicrosoftTts
        "d9537be74d23438ca581fd6d04e1d112", //EudictVocabulary
    ];
}
