using System;
using System.IO;
using System.Text.Json;

public sealed record ConfigLoadResult(Config Data, string ModuleDirectory, string ConfigDirectory, string ConfigFilePath, bool Created);

public static class Configs
{
    private const string ConfigFileName = "config.json";

    public static ConfigLoadResult Load(string? modulePath, bool saveAfterLoad = false)
    {
        var moduleDirectory = ResolveModuleDirectory(modulePath);
        var configDirectory = ResolveConfigDirectory(modulePath, moduleDirectory);
        Directory.CreateDirectory(configDirectory);

        var configFilePath = Path.Combine(configDirectory, ConfigFileName);
        var options = CreateSerializerOptions();
        var defaultConfig = new Config();
        var exists = File.Exists(configFilePath);

        var configData = exists
            ? DeserializeConfigData(configFilePath, options) ?? defaultConfig
            : defaultConfig;
        var created = !exists;

        if (saveAfterLoad && created)
        {
            SaveConfigData(configData, configFilePath);
        }

        return new ConfigLoadResult(configData, moduleDirectory, configDirectory, configFilePath, created);
    }

    private static Config? DeserializeConfigData(string path, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options);

    public static void SaveConfigData(Config configData, string configFilePath)
    {
        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(configFilePath, JsonSerializer.Serialize(configData, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string ResolveModuleDirectory(string? modulePath)
    {
        var directory = string.IsNullOrWhiteSpace(modulePath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(modulePath);

        if (string.IsNullOrWhiteSpace(directory))
            directory = Directory.GetCurrentDirectory();

        try
        {
            return Path.GetFullPath(directory);
        }
        catch
        {
            return directory;
        }
    }

    public static string ResolveConfigDirectory(string? modulePath)
    {
        var moduleDirectory = ResolveModuleDirectory(modulePath);
        return ResolveConfigDirectory(modulePath, moduleDirectory);
    }

    private static string ResolveConfigDirectory(string? modulePath, string moduleDirectory)
    {
        var moduleDirInfo = new DirectoryInfo(moduleDirectory);
        var pluginFolderName = DeterminePluginFolderName(modulePath, moduleDirInfo);

        for (var current = moduleDirInfo; current != null; current = current.Parent)
        {
            if (!current.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                continue;

            var cssRoot = current.Parent;
            var addonsRoot = cssRoot?.Parent;

            if (cssRoot?.Name.Equals("counterstrikesharp", StringComparison.OrdinalIgnoreCase) == true &&
                addonsRoot?.Name.Equals("addons", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Path.Combine(addonsRoot.FullName, "counterstrikesharp", "configs", "plugins", pluginFolderName);
            }
        }

        return Path.Combine(moduleDirectory, "config");
    }

    private static string DeterminePluginFolderName(string? modulePath, DirectoryInfo moduleDirInfo)
    {
        var moduleName = Path.GetFileNameWithoutExtension(modulePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            if (!string.Equals(moduleDirInfo.Name, "plugins", StringComparison.OrdinalIgnoreCase))
            {
                return moduleDirInfo.Name;
            }

            return moduleName;
        }

        return moduleDirInfo.Name;
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
