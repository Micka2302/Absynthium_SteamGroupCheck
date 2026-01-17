using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

[MinimumApiVersion(80)]
public class SteamGroupCheckPlugin : BasePlugin
{
    private const string PluginFolderName = "Absynthium_SteamGroupCheck";
    private const string LangFolderName = "lang";
    private const string DefaultLanguage = "en";

    public override string ModuleName => "Absynthium Steam Group Check";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "Absynthium";

    public Config Config { get; set; } = new();
    public MessageConfig Messages { get; set; } = new();

    private static readonly HttpClient HttpClient = new();
    private readonly ConcurrentDictionary<ulong, MembershipCacheEntry> _membershipCache = new();
    private readonly ConcurrentDictionary<ulong, PlayerMembershipSnapshot> _playerMembershipState = new();

    private CancellationTokenSource? _nonMemberAdCts;
    private Task? _nonMemberAdTask;

    private IReadOnlyList<ConfiguredGroup> _configuredGroups = new List<ConfiguredGroup>();
    private string _moduleDirectory = string.Empty;
    private string _configDirectory = string.Empty;
    private string _configFilePath = string.Empty;

    private static readonly Regex ColorTagRegex = new(@"\{/?[a-z0-9_]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ColorChar(char value) => value.ToString();

    private static readonly Func<CCSPlayerController?, string> DefaultColorResolver = _ => ColorChar(ChatColors.Default);

    private static readonly IReadOnlyDictionary<string, Func<CCSPlayerController?, string>> ColorTagResolvers =
        new Dictionary<string, Func<CCSPlayerController?, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = DefaultColorResolver,
            ["reset"] = DefaultColorResolver,
            ["white"] = _ => ColorChar(ChatColors.White),
            ["green"] = _ => ColorChar(ChatColors.Green),
            ["lightgreen"] = _ => ColorChar(ChatColors.Lime),
            ["lightyellow"] = _ => ColorChar(ChatColors.LightYellow),
            ["yellow"] = _ => ColorChar(ChatColors.Yellow),
            ["lightblue"] = _ => ColorChar(ChatColors.LightBlue),
            ["blue"] = _ => ColorChar(ChatColors.Blue),
            ["darkblue"] = _ => ColorChar(ChatColors.DarkBlue),
            ["olive"] = _ => ColorChar(ChatColors.Olive),
            ["lime"] = _ => ColorChar(ChatColors.Lime),
            ["red"] = _ => ColorChar(ChatColors.Red),
            ["lightred"] = _ => ColorChar(ChatColors.LightRed),
            ["darkred"] = _ => ColorChar(ChatColors.DarkRed),
            ["lightpurple"] = _ => ColorChar(ChatColors.LightPurple),
            ["purple"] = _ => ColorChar(ChatColors.Purple),
            ["magenta"] = _ => ColorChar(ChatColors.Magenta),
            ["grey"] = _ => ColorChar(ChatColors.Grey),
            ["gray"] = _ => ColorChar(ChatColors.Grey),
            ["gold"] = _ => ColorChar(ChatColors.Gold),
            ["silver"] = _ => ColorChar(ChatColors.Silver),
            ["orange"] = _ => ColorChar(ChatColors.Orange),
            ["team"] = player => ColorChar(player is not null ? ChatColors.ForPlayer(player) : ChatColors.Default),
            ["ct"] = _ => ColorChar(ChatColors.Blue),
            ["counterterrorist"] = _ => ColorChar(ChatColors.Blue),
            ["t"] = _ => ColorChar(ChatColors.Yellow),
            ["terrorist"] = _ => ColorChar(ChatColors.Yellow),
            ["spec"] = _ => ColorChar(ChatColors.LightPurple),
            ["spectator"] = _ => ColorChar(ChatColors.LightPurple),
        };

    private bool DebugEnabled => Config?.EnableDebugLogging ?? false;

    private void DebugLog(string message, params object?[] args)
    {
        if (DebugEnabled)
        {
            Logger.LogInformation(message, args ?? Array.Empty<object?>());
        }
    }

    private void DebugLog(Exception exception, string message, params object?[] args)
    {
        if (DebugEnabled)
        {
            Logger.LogInformation(exception, message, args ?? Array.Empty<object?>());
        }
    }

    public override void Load(bool hotReload)
    {
        LoadConfig();
        LoadLocalization();
        DebugLog("Load start hotReload={HotReload}", hotReload);

        HttpClient.Timeout = TimeSpan.FromSeconds(Config.RequestTimeoutSeconds);
        HttpClient.DefaultRequestHeaders.UserAgent.Clear();
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{PluginFolderName}/1.0");

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnClientDisconnectPost>(OnClientDisconnect);
        AddCommand("group_check", "Report group membership", OnGroupCheckCommand);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);

        RestartNonMemberAdBroadcast();
    }

    public override void Unload(bool hotReload)
    {
        StopNonMemberAdBroadcast();
        base.Unload(hotReload);
    }

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        var length = value.Length;
        if (length <= 4)
            return new string('*', length);

        var suffix = value.Substring(length - 4);
        return new string('*', length - 4) + suffix;
    }

    private string ResolveLanguagePath(string language)
    {
        var sanitized = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        var moduleDir = string.IsNullOrWhiteSpace(_moduleDirectory)
            ? Configs.ResolveModuleDirectory(ModulePath)
            : _moduleDirectory;
        var configDir = string.IsNullOrWhiteSpace(_configDirectory)
            ? Configs.ResolveConfigDirectory(ModulePath)
            : _configDirectory;

        var pluginLangDir = Path.Combine(moduleDir, LangFolderName);
        var pluginPath = Path.Combine(pluginLangDir, $"{sanitized}.json");
        var configPath = Path.Combine(configDir, LangFolderName, $"{sanitized}.json");

        // Prefer the language file next to the DLL.
        if (File.Exists(pluginPath))
            return pluginPath;

        // If only the config copy exists, migrate it beside the DLL.
        if (File.Exists(configPath))
        {
            try
            {
                Directory.CreateDirectory(pluginLangDir);
                File.Copy(configPath, pluginPath, overwrite: true);
                Logger.LogInformation("Migrated language file from config path {ConfigPath} to plugin path {PluginPath}", configPath, pluginPath);
                return pluginPath;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to migrate language file from config path {ConfigPath} to plugin path {PluginPath}; continuing to use config path", configPath, pluginPath);
                return configPath;
            }
        }

        return pluginPath;
    }

    private void LoadConfig()
    {
        try
        {
            var loadResult = Configs.Load(ModulePath, saveAfterLoad: true);
            _moduleDirectory = loadResult.ModuleDirectory;
            _configDirectory = loadResult.ConfigDirectory;
            _configFilePath = loadResult.ConfigFilePath;

            var defaultConfig = new Config();
            Config = loadResult.Data ?? defaultConfig;

            var updated = NormalizeConfig(Config, defaultConfig);
            _configuredGroups = BuildConfiguredGroups(Config, defaultConfig);

            if (_configuredGroups.Count == 0)
            {
                Logger.LogWarning("No valid Steam groups configured. Players will not be matched until at least one group is added.");
            }

            var maskedApiKey = MaskSecret(Config.SteamApiKey);
            DebugLog("Loaded config from {ConfigPath} (SteamApiKeyMasked={ApiKey}, Language={Language}, Timeout={Timeout}s, Cache={Cache}s, AdInterval={AdInterval}m, Groups={GroupCount})", _configFilePath, maskedApiKey, Config.Language, Config.RequestTimeoutSeconds, Config.CacheDurationSeconds, Config.NonMemberAdIntervalMinutes, _configuredGroups.Count);

            if (updated && !loadResult.Created)
            {
                Configs.SaveConfigData(Config, _configFilePath);
                Logger.LogInformation("Patched config at {ConfigPath} with missing or invalid fields.", _configFilePath);
            }
            else if (loadResult.Created)
            {
                Logger.LogWarning("Config file not found at {ConfigPath}. Created default config.", _configFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load config");
            Config = new Config();
            _configuredGroups = BuildConfiguredGroups(Config, new Config());
            _moduleDirectory = Configs.ResolveModuleDirectory(ModulePath);
            _configDirectory = Configs.ResolveConfigDirectory(ModulePath);
            _configFilePath = Path.Combine(_configDirectory, "config.json");
        }
    }

    private void LoadLocalization()
    {
        try
        {
            var langPath = ResolveLanguagePath(Config.Language ?? DefaultLanguage);
            var directory = Path.GetDirectoryName(langPath);
            var defaultMessages = new MessageConfig();

            if (!File.Exists(langPath))
            {
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Messages = defaultMessages;
                File.WriteAllText(langPath, JsonSerializer.Serialize(Messages, new JsonSerializerOptions { WriteIndented = true }));
                Logger.LogWarning("Language file not found at {LangPath}. Created default language file.", langPath);
                return;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var json = File.ReadAllText(langPath);
            var loaded = JsonSerializer.Deserialize<MessageConfig>(json, options);
            Messages = loaded ?? defaultMessages;

            if (Messages.ApplyDefaultsFrom(defaultMessages))
            {
                File.WriteAllText(langPath, JsonSerializer.Serialize(Messages, new JsonSerializerOptions { WriteIndented = true }));
                Logger.LogInformation("Patched language file at {LangPath} with missing entries.", langPath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load language file for language {Language}", Config.Language);
            Messages = new MessageConfig();
        }
    }

    private void OnClientAuthorized(int playerSlot, SteamID steamId)
    {
        DebugLog("OnClientAuthorized slot={Slot} steamId={SteamId}", playerSlot, steamId.SteamId64);
        _ = HandleClientAuthorizedAsync(playerSlot, steamId);
    }

    private async Task HandleClientAuthorizedAsync(int playerSlot, SteamID steamId)
    {
        DebugLog("HandleClientAuthorizedAsync for {SteamId}", steamId.SteamId64);

        var membership = await GetGroupMembershipAsync(steamId.SteamId64);

        DebugLog("Player {SteamId} membership groups: {Membership}", steamId.SteamId64, string.Join(", ", membership.MemberGroupIds));
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null)
            {
                DebugLog("Player slot {Slot} went missing before membership apply for {SteamId}", playerSlot, steamId.SteamId64);
                return;
            }

            var currentSteamId = player.AuthorizedSteamID?.SteamId64 ?? 0;
            if (currentSteamId != steamId.SteamId64)
            {
                DebugLog("Skipping membership apply for {SteamId} because slot {Slot} now belongs to {CurrentSteamId}", steamId.SteamId64, playerSlot, currentSteamId);
                return;
            }

            _ = ApplyMembershipAsync(player, membership);
        });
    }

    private async Task RespondGroupStatusAsync(CCSPlayerController player)
    {
        var steamId64 = player.AuthorizedSteamID?.SteamId64 ?? 0;
        DebugLog("RespondGroupStatusAsync for {SteamId}", steamId64);
        if (steamId64 == 0)
        {
            SendChatMessage(player, Messages.SteamIdUnavailable, steamId64);
            return;
        }

        var membership = await GetGroupMembershipAsync(steamId64);

        if (membership.Success && membership.MemberGroupIds.Count > 0)
        {
            SendChatMessage(player, Messages.GroupCheckMember, steamId64, membership.MemberGroupIds);
        }
        else if (membership.Success)
        {
            SendChatMessage(player, Messages.GroupCheckNotMember, steamId64, membership.MemberGroupIds);
        }
        else
        {
            SendChatMessage(player, Messages.GroupCheckUnknown, steamId64, membership.MemberGroupIds);
        }
    }

    private void OnGroupCheckCommand(CCSPlayerController? player, CommandInfo command)
    {
        DebugLog("!group_check invoked by {SteamId}", player?.AuthorizedSteamID?.SteamId64 ?? 0);
        if (player == null) return;

        var task = RespondGroupStatusAsync(player);
        if (!task.IsCompleted)
        {
            task.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    Logger.LogError(t.Exception, "RespondGroupStatusAsync failed for {SteamId}", player.AuthorizedSteamID?.SteamId64 ?? 0);
                }
            }, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted);
        }
        else if (task.IsFaulted)
        {
            Logger.LogError(task.Exception!, "RespondGroupStatusAsync failed for {SteamId}", player.AuthorizedSteamID?.SteamId64 ?? 0);
        }
    }

    private void OnClientDisconnect(int playerSlot)
    {
        DebugLog("OnClientDisconnect slot={Slot}", playerSlot);
        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player?.AuthorizedSteamID != null)
        {
            var steamId = player.AuthorizedSteamID.SteamId64;

            if (_playerMembershipState.TryRemove(steamId, out var snapshot))
            {
                var flags = snapshot.GrantedFlags;
                if (flags.Count > 0)
                {
                    Server.NextFrame(() =>
                    {
                        try
                        {
                            foreach (var flag in flags)
                            {
                                AdminManager.RemovePlayerPermissions(player, flag);
                                Logger.LogInformation("Revoked {Flag} from {SteamId}", flag, steamId);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to revoke flags for {SteamId}", steamId);
                        }
                    });
                }
            }
        }
    }

    private void SendChatMessage(CCSPlayerController player, string? template, ulong steamId64, IReadOnlyCollection<ulong>? memberGroupIds = null)
    {
        Server.NextFrame(() =>
        {
            string message;
            try
            {
                message = FormatMessage(template, player, steamId64, memberGroupIds);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to format chat message for {SteamId}", steamId64);
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                DebugLog("Chat message -> {SteamId}: {MessageEscaped}", steamId64, EscapeControlChars(message));
                player.PrintToChat(message);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to send chat message to {SteamId}", steamId64);
            }
        });
    }

    private string FormatMessage(string? template, CCSPlayerController? player, ulong steamId64, IReadOnlyCollection<ulong>? memberGroupIds = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var prefix = Messages?.Prefix ?? string.Empty;
        var effectiveTemplate = string.Concat(prefix, template);

        var memberGroups = memberGroupIds != null && memberGroupIds.Count > 0
            ? _configuredGroups.Where(g => memberGroupIds.Contains(g.GroupId)).ToList()
            : new List<ConfiguredGroup>();

        var primaryGroup = GetPrimaryGroup(memberGroupIds);
        var allGroupsList = BuildGroupList(_configuredGroups);
        var allGroupUrls = BuildGroupUrlList(_configuredGroups);

        var memberList = memberGroups.Count > 0 ? BuildGroupList(memberGroups) : allGroupsList;
        var memberUrls = memberGroups.Count > 0 ? BuildGroupUrlList(memberGroups) : allGroupUrls;
        var primaryList = primaryGroup is not null ? new List<ConfiguredGroup> { primaryGroup } : memberGroups;
        var flagList = BuildFlagList(primaryList);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{PlayerName}"] = player?.PlayerName ?? string.Empty,
            ["{SteamId64}"] = steamId64 == 0 ? string.Empty : steamId64.ToString(CultureInfo.InvariantCulture),
            ["{GroupId}"] = primaryGroup?.GroupId.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["{GroupUrl}"] = primaryGroup?.GroupUrl ?? string.Empty,
            ["{GroupList}"] = allGroupsList,
            ["{GroupUrls}"] = allGroupUrls,
            ["{MemberGroups}"] = memberList,
            ["{MemberGroupUrls}"] = memberUrls,
            ["{Flag}"] = flagList,
            ["{Flags}"] = flagList
        };

        var result = effectiveTemplate;
        foreach (var pair in replacements)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        result = result.ReplaceColorTags();

        var colored = ApplyColorMarkup(result, player);

        // Ensure a leading default color control so Source renders colour codes reliably.
        if (!string.IsNullOrEmpty(colored) && colored[0] != ChatColors.Default)
        {
            colored = ColorChar(ChatColors.Default) + colored;
        }

        return colored;
    }

    private static string EscapeControlChars(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var chars = value.Select(ch =>
        {
            if (char.IsControl(ch))
                return $"<0x{((int)ch):X2}>";
            return ch.ToString();
        });

        return string.Concat(chars);
    }

    private string ApplyColorMarkup(string message, CCSPlayerController? player)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? string.Empty;

        return ColorTagRegex.Replace(message, match =>
        {
            var token = match.Value.Substring(1, match.Value.Length - 2).Trim();
            if (token.Length == 0)
                return match.Value;

            if (token.StartsWith("/", StringComparison.Ordinal))
                return DefaultColorResolver(player);

            return ColorTagResolvers.TryGetValue(token, out var resolver)
                ? resolver(player)
                : match.Value;
        });
    }

    private static string BuildGroupList(IEnumerable<ConfiguredGroup> groups)
    {
        var names = groups?.Select(g => g.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        return names.Count == 0 ? "N/A" : string.Join(", ", names);
    }

    private static string BuildGroupUrlList(IEnumerable<ConfiguredGroup> groups)
    {
        var names = groups?.Select(g => $"{g.DisplayName}: {g.GroupUrl}").ToList() ?? new List<string>();
        return names.Count == 0 ? "https://steamcommunity.com" : string.Join(", ", names);
    }

    private static string BuildFlagList(IEnumerable<ConfiguredGroup> groups)
    {
        var flags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups ?? Enumerable.Empty<ConfiguredGroup>())
        {
            foreach (var flag in group.GrantedFlags ?? Array.Empty<string>())
            {
                if (seen.Add(flag))
                {
                    flags.Add(flag);
                }
            }
        }

        return flags.Count == 0 ? string.Empty : string.Join(", ", flags);
    }

    private void StopNonMemberAdBroadcast()
    {
        var cts = _nonMemberAdCts;
        var task = _nonMemberAdTask;
        _nonMemberAdCts = null;
        _nonMemberAdTask = null;

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                DebugLog(ex, "Failed to cancel non-member advertisement broadcaster");
            }
        }

        if (task != null)
        {
            task.ContinueWith(t =>
            {
                try
                {
                    cts?.Dispose();
                }
                catch (Exception disposeEx)
                {
                    DebugLog(disposeEx, "Failed to dispose non-member advertisement cancellation token");
                }

                if (t.IsFaulted && t.Exception != null)
                {
                    Logger.LogError(t.Exception, "Non-member advertisement loop faulted");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        else
        {
            cts?.Dispose();
        }
    }

    private void RestartNonMemberAdBroadcast()
    {
        StopNonMemberAdBroadcast();

        var intervalMinutes = Config?.NonMemberAdIntervalMinutes ?? 0;
        var template = Messages?.NonMemberAdvertisement;

        if (intervalMinutes <= 0 || string.IsNullOrWhiteSpace(template))
        {
            DebugLog("Non-member advertisement disabled (interval={Interval}, hasTemplate={HasTemplate})", intervalMinutes, !string.IsNullOrWhiteSpace(template));
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        var templateValue = template!;

        var cts = new CancellationTokenSource();
        _nonMemberAdCts = cts;
        _nonMemberAdTask = Task.Run(() => RunNonMemberAdLoopAsync(templateValue, interval, cts.Token));

        DebugLog("Non-member advertisement loop scheduled (interval={Interval})", interval);
    }

    private async Task RunNonMemberAdLoopAsync(string template, TimeSpan interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                BroadcastNonMemberAdvertisement(template);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Non-member advertisement loop encountered an error");
        }
    }

    private void BroadcastNonMemberAdvertisement(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return;

        try
        {
            Server.NextFrame(() =>
            {
                try
                {
                    foreach (var player in Utilities.GetPlayers())
                    {
                        if (player?.AuthorizedSteamID?.SteamId64 is not ulong steamId || steamId == 0)
                            continue;

                        if (!_playerMembershipState.TryGetValue(steamId, out var state))
                            continue;

                        if (state.HasMembership)
                            continue;

                        SendChatMessage(player, template, steamId, state.MemberGroupIds);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed during non-member advertisement broadcast");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to queue non-member advertisement broadcast");
        }
    }

    private void ScheduleFlagGrant(ulong steamId, string flag, int attempt = 0)
    {
        const int maxAttempts = 3;
        if (attempt >= maxAttempts)
            return;

        Server.NextFrame(() =>
        {
            if (!_playerMembershipState.TryGetValue(steamId, out var state) || !ContainsFlag(state.GrantedFlags, flag))
            {
                DebugLog("Skipping flag grant retry for {SteamId} (state changed)", steamId);
                return;
            }

            var player = Utilities.GetPlayers()
                .FirstOrDefault(p => p?.AuthorizedSteamID?.SteamId64 == steamId);
            if (player == null)
            {
                DebugLog("Unable to resolve player for flag grant attempt {Attempt} on {SteamId}", attempt, steamId);
                return;
            }

            try
            {
                AdminManager.AddPlayerPermissions(player, flag);
                if (attempt == 0)
                {
                    Logger.LogInformation("Granted {Flag} to {SteamId}", flag, steamId);
                }
                else
                {
                    DebugLog("Reapplied {Flag} to {SteamId} (attempt {Attempt})", flag, steamId, attempt);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to apply {Flag} to {SteamId} on attempt {Attempt}", flag, steamId, attempt);
            }
        });

        if (attempt + 1 >= maxAttempts)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
                ScheduleFlagGrant(steamId, flag, attempt + 1);
            }
            catch (Exception ex)
            {
                DebugLog(ex, "Failed to schedule flag grant retry for {SteamId}", steamId);
            }
        });
    }

    private static bool ContainsFlag(IReadOnlyCollection<string> flags, string flag)
    {
        return flags.Any(existing => string.Equals(existing, flag, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<MembershipResult> GetGroupMembershipAsync(ulong steamId)
    {
        DebugLog("GetGroupMembershipAsync for {SteamId}", steamId);

        if (steamId == 0)
            return MembershipResult.Unknown;

        if (TryGetCachedMembership(steamId, out var cached))
            return cached;

        var membership = await QueryGroupMembershipAsync(steamId);
        StoreCachedMembership(steamId, membership);
        return membership;
    }

    private bool TryGetCachedMembership(ulong steamId, out MembershipResult membership)
    {
        membership = MembershipResult.Unknown;
        if (_membershipCache.TryGetValue(steamId, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                membership = new MembershipResult(true, new HashSet<ulong>(entry.MemberGroupIds));
                DebugLog("Cache hit for {SteamId} (membership groups={Groups})", steamId, string.Join(", ", membership.MemberGroupIds));
                return true;
            }

            _membershipCache.TryRemove(steamId, out _);
            DebugLog("Cache expired for {SteamId}", steamId);
        }
        return false;
    }

    private void StoreCachedMembership(ulong steamId, MembershipResult membership)
    {
        var durationSeconds = Math.Max(0, Config.CacheDurationSeconds);
        if (durationSeconds <= 0 || !membership.Success || membership.MemberGroupIds.Count == 0)
            return;

        var ttl = TimeSpan.FromSeconds(Math.Max(5, durationSeconds));
        var entry = new MembershipCacheEntry(new HashSet<ulong>(membership.MemberGroupIds), DateTimeOffset.UtcNow.Add(ttl));
        _membershipCache[steamId] = entry;
        DebugLog("Cache store for {SteamId} (membership groups={Groups}, ttl={Ttl})", steamId, string.Join(", ", entry.MemberGroupIds), ttl);
    }

    private async Task<MembershipResult> QueryGroupMembershipAsync(ulong steamId)
    {
        DebugLog("QueryGroupMembershipAsync for {SteamId}", steamId);

        if (string.IsNullOrWhiteSpace(Config.SteamApiKey))
        {
            Logger.LogWarning("Invalid configuration: SteamApiKey is missing");
            return MembershipResult.Unknown;
        }

        if (_configuredGroups.Count == 0)
        {
            Logger.LogWarning("No Steam groups configured; cannot validate membership.");
            return MembershipResult.Unknown;
        }

        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetUserGroupList/v1/?key={Config.SteamApiKey}&steamid={steamId}";
            using var response = await HttpClient.GetAsync(url);
            string body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to read Steam API response body for {SteamId}", steamId);
            }
            var privateProfileResponse = !string.IsNullOrWhiteSpace(body) &&
                body.IndexOf("Private profile", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden && privateProfileResponse)
                {
                    Logger.LogInformation("Steam API denied group list for {SteamId} because the profile is private; treating as not a member. Body: {Body}", steamId, body);
                    return new MembershipResult(true, Array.Empty<ulong>());
                }

                Logger.LogWarning("Steam API returned {StatusCode} for {SteamId}. Body: {Body}", response.StatusCode, steamId, body);
                return MembershipResult.Unknown;
            }
            else
            {
                DebugLog("Steam API success {StatusCode} for {SteamId}. Body: {Body}", response.StatusCode, steamId, body);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("response", out var resp))
            {
                bool successFlag = true;
                if (resp.TryGetProperty("success", out var successEl))
                {
                    switch (successEl.ValueKind)
                    {
                        case JsonValueKind.True:
                            successFlag = true; break;
                        case JsonValueKind.False:
                            successFlag = false; break;
                        case JsonValueKind.Number:
                            try { successFlag = successEl.GetInt32() == 1; } catch { successFlag = false; }
                            break;
                        case JsonValueKind.String:
                            var s = successEl.GetString();
                            if (int.TryParse(s, out var n)) successFlag = n == 1;
                            else if (bool.TryParse(s, out var b)) successFlag = b;
                            break;
                        default:
                            successFlag = false; break;
                    }
                }

                string? errorMessage = null;
                if (resp.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                {
                    errorMessage = errorEl.GetString();
                }

                if (!successFlag)
                {
                    if (!string.IsNullOrWhiteSpace(errorMessage))
                    {
                        Logger.LogInformation("Steam API response error '{Error}' for {SteamId}; treating as not a member", errorMessage, steamId);
                    }
                    else if (privateProfileResponse)
                    {
                        Logger.LogInformation("Steam API response indicates private profile for {SteamId}; treating as not a member", steamId);
                    }

                    return new MembershipResult(true, Array.Empty<ulong>());
                }

                if (resp.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                {
                    var matchedGroupIds = new HashSet<ulong>();
                    var configuredLookup = new HashSet<ulong>(_configuredGroups.Select(g => g.GroupId));
                    foreach (var group in groups.EnumerateArray())
                    {
                        if (!group.TryGetProperty("gid", out var gidEl))
                            continue;

                        if (TryParseGroupId(gidEl, out var gidUlong) && configuredLookup.Contains(gidUlong))
                            matchedGroupIds.Add(gidUlong);
                    }

                    return new MembershipResult(true, matchedGroupIds);
                }

                return new MembershipResult(true, Array.Empty<ulong>());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Failed to check group membership for {SteamId}", steamId);
        }
        return MembershipResult.Unknown;
    }

    private static bool TryParseGroupId(JsonElement gidEl, out ulong gid)
    {
        gid = 0;
        if (gidEl.ValueKind == JsonValueKind.String)
        {
            var gidStr = gidEl.GetString();
            if (ulong.TryParse(gidStr, out var parsed))
                gid = parsed;
        }
        else if (gidEl.ValueKind == JsonValueKind.Number)
        {
            try { gid = gidEl.GetUInt64(); } catch { gid = 0; }
        }

        return gid != 0;
    }

    private Task ApplyMembershipAsync(CCSPlayerController player, MembershipResult membership)
    {
        var steamId = player.AuthorizedSteamID?.SteamId64 ?? 0;

        DebugLog("ApplyMembershipAsync steamId={SteamId} groups={Groups}", steamId, string.Join(", ", membership.MemberGroupIds));

        if (steamId == 0)
            return Task.CompletedTask;

        var memberGroups = membership.MemberGroupIds ?? Array.Empty<ulong>();
        var requiredFlags = GetFlagsForGroups(memberGroups);

        if (_playerMembershipState.TryGetValue(steamId, out var current) &&
            SetsEqual(current.MemberGroupIds, memberGroups) &&
            SetsEqual(current.GrantedFlags, requiredFlags))
        {
            return Task.CompletedTask;
        }

        var currentFlags = current?.GrantedFlags ?? Array.Empty<string>();

        var flagsToAdd = requiredFlags.Except(currentFlags, StringComparer.OrdinalIgnoreCase).ToList();
        var flagsToRemove = currentFlags.Except(requiredFlags, StringComparer.OrdinalIgnoreCase).ToList();

        DebugLog("Membership apply for {SteamId}: groups={Groups} flagsToAdd={Add} flagsToRemove={Remove}", steamId, string.Join(", ", memberGroups), string.Join(", ", flagsToAdd), string.Join(", ", flagsToRemove));

        foreach (var flag in flagsToAdd)
        {
            ScheduleFlagGrant(steamId, flag);
        }

        if (flagsToRemove.Count > 0)
        {
            Server.NextFrame(() =>
            {
                try
                {
                    var target = Utilities.GetPlayers()
                        .FirstOrDefault(p => p?.AuthorizedSteamID?.SteamId64 == steamId);
                    if (target != null)
                    {
                        foreach (var flag in flagsToRemove)
                        {
                            AdminManager.RemovePlayerPermissions(target, flag);
                            Logger.LogInformation("Revoked {Flag} from {SteamId}", flag, steamId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to remove flags from {SteamId}", steamId);
                }
            });
        }

        if (memberGroups.Count > 0 && (current == null || current.MemberGroupIds.Count == 0))
        {
            SendChatMessage(player, Messages.MemberDetected, steamId, memberGroups);
        }

        var newSnapshot = new PlayerMembershipSnapshot(
            new HashSet<ulong>(memberGroups),
            new HashSet<string>(requiredFlags, StringComparer.OrdinalIgnoreCase));

        _playerMembershipState[steamId] = newSnapshot;

        return Task.CompletedTask;
    }

    private IReadOnlyCollection<string> GetFlagsForGroups(IReadOnlyCollection<ulong> memberGroupIds)
    {
        if (memberGroupIds == null || memberGroupIds.Count == 0)
            return Array.Empty<string>();

        var primary = GetPrimaryGroup(memberGroupIds);
        if (primary is null || primary.GrantedFlags.Count == 0)
            return Array.Empty<string>();

        return primary.GrantedFlags;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        try
        {
            foreach (var player in Utilities.GetPlayers())
            {
                var steamId = player?.AuthorizedSteamID?.SteamId64 ?? 0;
                if (steamId == 0)
                    continue;

                if (!_playerMembershipState.TryGetValue(steamId, out var snapshot) || snapshot.GrantedFlags.Count == 0)
                    continue;

                var membership = new MembershipResult(true, snapshot.MemberGroupIds);
                _ = ApplyMembershipAsync(player!, membership);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reapply flags on round start");
        }

        return HookResult.Continue;
    }

    private static bool SetsEqual(IReadOnlyCollection<ulong> first, IReadOnlyCollection<ulong> second)
    {
        if (first.Count != second.Count)
            return false;

        return new HashSet<ulong>(first).SetEquals(second);
    }

    private static bool SetsEqual(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
    {
        if (first.Count != second.Count)
            return false;

        return new HashSet<string>(first, StringComparer.OrdinalIgnoreCase).SetEquals(second);
    }

    [ConsoleCommand("css_steamgroupcheck_reload", "Reload Absynthium SteamGroupCheck config")]
    public void ReloadConfig(CCSPlayerController? player, CommandInfo command)
    {
        Logger.LogInformation("ReloadConfig command invoked");

        LoadConfig();
        LoadLocalization();
        HttpClient.Timeout = TimeSpan.FromSeconds(Config.RequestTimeoutSeconds);
        _membershipCache.Clear();
        DebugLog("Cleared membership cache after config reload");

        foreach (var current in Utilities.GetPlayers())
        {
            var steamId = current?.AuthorizedSteamID;
            if (steamId is not null && steamId.SteamId64 != 0)
            {
                _ = HandleClientAuthorizedAsync(current!.Slot, steamId);
            }
        }

        RestartNonMemberAdBroadcast();

        command.ReplyToCommand("Absynthium_SteamGroupCheck config reloaded");
    }

    private static List<SteamGroupConfig>? AttemptLegacyMigration(Config config, Config defaults)
    {
        if (config.Extra == null)
            return null;

        string? legacyGroupId = null;
        string? legacyFlag = null;

        if (config.Extra.TryGetValue("TargetSteamGroupId", out var groupEl))
        {
            legacyGroupId = ReadJsonElementAsString(groupEl);
        }

        if (config.Extra.TryGetValue("GrantedFlag", out var flagEl))
        {
            legacyFlag = ReadJsonElementAsString(flagEl);
        }

        if (legacyGroupId == null && legacyFlag == null)
            return null;

        var fallbackGroup = defaults.Groups.FirstOrDefault() ?? new SteamGroupConfig();
        return new List<SteamGroupConfig>
        {
            new SteamGroupConfig
            {
                GroupId = string.IsNullOrWhiteSpace(legacyGroupId) ? fallbackGroup.GroupId : legacyGroupId,
                GrantedFlag = string.IsNullOrWhiteSpace(legacyFlag) ? fallbackGroup.GrantedFlag : legacyFlag
            }
        };
    }

    private static string? ReadJsonElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool NormalizeConfig(Config config, Config defaults)
    {
        var updated = false;

        if (string.IsNullOrWhiteSpace(config.Language))
        {
            config.Language = defaults.Language;
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(config.SteamApiKey))
        {
            config.SteamApiKey = defaults.SteamApiKey;
            updated = true;
        }

        if (config.RequestTimeoutSeconds <= 0)
        {
            config.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
            updated = true;
        }

        if (config.CacheDurationSeconds <= 0)
        {
            config.CacheDurationSeconds = defaults.CacheDurationSeconds;
            updated = true;
        }

        if (config.NonMemberAdIntervalMinutes < 0)
        {
            config.NonMemberAdIntervalMinutes = defaults.NonMemberAdIntervalMinutes;
            updated = true;
        }

        if (config.Groups == null || config.Groups.Count == 0)
        {
            config.Groups = AttemptLegacyMigration(config, defaults) ?? defaults.Groups;
            updated = true;
        }
        else
        {
            var fallbackGroup = defaults.Groups.FirstOrDefault() ?? new SteamGroupConfig();
            foreach (var group in config.Groups)
            {
                if (group == null)
                    continue;

                if (string.IsNullOrWhiteSpace(group.GroupId))
                {
                    group.GroupId = fallbackGroup.GroupId;
                    updated = true;
                }

                if (string.IsNullOrWhiteSpace(group.GrantedFlag))
                {
                    group.GrantedFlag = fallbackGroup.GrantedFlag;
                    updated = true;
                }

                if (group.Name != null && string.IsNullOrWhiteSpace(group.Name))
                {
                    group.Name = null;
                    updated = true;
                }
            }
        }

        return updated;
    }

    private IReadOnlyList<ConfiguredGroup> BuildConfiguredGroups(Config config, Config defaults)
    {
        var list = new List<ConfiguredGroup>();
        var fallback = defaults.Groups.FirstOrDefault() ?? new SteamGroupConfig();
        var configIndex = 0;

        if (config.Groups == null)
            return list;

        foreach (var group in config.Groups)
        {
            var currentIndex = configIndex++;

            if (group is null)
                continue;

            var rawId = string.IsNullOrWhiteSpace(group.GroupId) ? fallback.GroupId : group.GroupId;
            if (!ulong.TryParse(rawId, out var parsedId))
            {
                Logger.LogWarning("Skipping Steam group with invalid id '{GroupId}'", rawId);
                continue;
            }

            var flag = string.IsNullOrWhiteSpace(group.GrantedFlag) ? fallback.GrantedFlag : group.GrantedFlag;
            var parsedFlags = ParseFlags(flag ?? string.Empty);
            var displayName = string.IsNullOrWhiteSpace(group.Name) ? rawId : group.Name.Trim();
            var priority = group.Priority;

            list.Add(new ConfiguredGroup(parsedId, displayName, parsedFlags, rawId, priority, currentIndex));
        }

        return list
            .OrderByDescending(g => g.Priority)
            .ThenBy(g => g.ConfigIndex)
            .ToList();
    }

    private ConfiguredGroup? GetPrimaryGroup(IReadOnlyCollection<ulong>? memberGroupIds)
    {
        if (memberGroupIds != null && memberGroupIds.Count > 0)
        {
            var lookup = new HashSet<ulong>(memberGroupIds);
            var match = _configuredGroups.FirstOrDefault(g => lookup.Contains(g.GroupId));
            if (match is not null)
                return match;
        }

        return _configuredGroups.FirstOrDefault();
    }

    private static IReadOnlyList<string> ParseFlags(string rawFlags)
    {
        if (string.IsNullOrWhiteSpace(rawFlags))
            return Array.Empty<string>();

        var flags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var split = rawFlags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var flag in split)
        {
            if (string.IsNullOrWhiteSpace(flag))
                continue;

            var cleaned = flag.Trim();
            if (seen.Add(cleaned))
            {
                flags.Add(cleaned);
            }
        }

        return flags;
    }

    private sealed record ConfiguredGroup(ulong GroupId, string DisplayName, IReadOnlyList<string> GrantedFlags, string RawGroupId, int Priority, int ConfigIndex)
    {
        public string GroupUrl => $"https://steamcommunity.com/gid/{GroupId}";
    }

    private readonly struct MembershipCacheEntry
    {
        public MembershipCacheEntry(HashSet<ulong> memberGroupIds, DateTimeOffset expiresAt)
        {
            MemberGroupIds = memberGroupIds;
            ExpiresAt = expiresAt;
        }

        public HashSet<ulong> MemberGroupIds { get; }
        public DateTimeOffset ExpiresAt { get; }
    }

    private sealed record PlayerMembershipSnapshot(IReadOnlyCollection<ulong> MemberGroupIds, IReadOnlyCollection<string> GrantedFlags)
    {
        public bool HasMembership => MemberGroupIds.Count > 0;
    }

    private sealed record MembershipResult(bool Success, IReadOnlyCollection<ulong> MemberGroupIds)
    {
        public static MembershipResult Unknown { get; } = new(false, Array.Empty<ulong>());
    }
}
