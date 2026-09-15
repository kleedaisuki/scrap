using System.Globalization;
using System.Text.Json;
using Scrap.Gui.Models;

namespace Scrap.Gui.Services;

/// <summary>
/// 用户可持久化的显示偏好；搜索和秘密数据绝不写入此文件。
/// Persisted display preferences; searches and secret data are never written to this file.
/// </summary>
/// <param name="Theme">主题选择。Theme selection.</param>
/// <param name="Language">语言选择。Language selection.</param>
public sealed record UserPreferences(AppTheme Theme, AppLanguage Language)
{
    /// <summary>根据操作系统区域创建默认值。Creates defaults from the operating-system locale.</summary>
    public static UserPreferences CreateDefault() => new(
        AppTheme.System,
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English);
}

/// <summary>只负责加载和保存非敏感界面偏好。Loads and saves only non-sensitive UI preferences.</summary>
public sealed class UserPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    /// <summary>创建使用平台应用数据目录的偏好存储。Creates a store in the platform application-data directory.</summary>
    public UserPreferenceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MoeSegFault",
            "Scrap",
            "preferences.json"))
    {
    }

    /// <summary>创建使用指定路径的存储，便于测试。Creates a store at an explicit path for tests.</summary>
    /// <param name="path">JSON 文件路径。JSON file path.</param>
    public UserPreferenceStore(string path) => _path = path;

    /// <summary>读取偏好；文件缺失或损坏时回到安全默认值。Loads preferences, returning defaults for missing or malformed files.</summary>
    public UserPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UserPreferences.CreateDefault();
            }

            UserPreferences? preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_path), JsonOptions);
            return preferences is not null &&
                   Enum.IsDefined(preferences.Theme) &&
                   Enum.IsDefined(preferences.Language)
                ? preferences
                : UserPreferences.CreateDefault();
        }
        catch (IOException)
        {
            return UserPreferences.CreateDefault();
        }
        catch (JsonException)
        {
            return UserPreferences.CreateDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return UserPreferences.CreateDefault();
        }
    }

    /// <summary>尽力原子保存偏好；写入失败不应阻塞主要工作流。Best-effort atomically saves preferences without blocking the primary workflow.</summary>
    public void Save(UserPreferences preferences)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (IOException)
        {
            // 偏好是可选状态；只读用户目录不得阻断保险箱。 / Preferences are optional; a read-only profile must not disable the vault.
        }
        catch (UnauthorizedAccessException)
        {
            // 偏好是可选状态；受限用户目录仍可使用内存中的选择。 / Preferences are optional; a locked-down profile keeps working in memory.
        }
    }
}
