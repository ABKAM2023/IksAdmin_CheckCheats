using System.Globalization;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using IksAdminApi;
using MenuManager;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace IksAdminCheckCheatsPlugin;

[MinimumApiVersion(80)]
public class IksAdminCheckCheatsPlugin : BasePlugin, IPluginConfig<IksAdminCheckCheatsConfig>
{
    public override string ModuleName => "[IksAdmin] Check Cheats";
    public override string ModuleAuthor => "ABKAM";
    public override string ModuleVersion => "1.0.0";

    private readonly PluginCapability<IMenuApi?> _menuCapability = new("menu:nfcore");
    private IMenuApi? _menuApi;

    public static PluginCapability<IIksAdminApi> AdminApiCapability = new("iksadmin:core");
    private IIksAdminApi? _api;

    private readonly Dictionary<ulong, int> _messageDisplayTimes = new();
    private readonly Dictionary<ulong, int> _uncheckMessages = new();
    private static readonly HttpClient HttpClient = new();
    private readonly HashSet<ulong> _playersUnderCheck = new();
    private readonly Dictionary<ulong, Timer> _playerTimers = new();
    private readonly Dictionary<ulong, int> _remainingTimes = new();
    private readonly HashSet<ulong> _contactProvided = new();
    private readonly HashSet<ulong> _hiddenMessagesForAdmins = new();

    private readonly Dictionary<ulong, (string PlayerName, int TimeLeft, ulong AdminSteamId)>
        _adminCheckMessages = new();

    public IksAdminCheckCheatsConfig Config { get; set; }

    private string? _chatPrefix;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _menuApi = _menuCapability.Get();
            _api = AdminApiCapability.Get();
            if (_api != null)
            {
                var checkMenuOption = new AdminMenuOption(
                    Localizer["select_player_for_check"],
                    "",
                    Config.Flag,
                    "ManagePlayers",
                    (admin, _, _) => ShowCheckMenu(admin)
                );

                var uncheckMenuOption = new AdminMenuOption(
                    Localizer["select_player_to_uncheck"],
                    "",
                    Config.Flag,
                    "ManagePlayers",
                    (admin, _, _) => ShowUncheckMenu(admin)
                );

                _api.ModulesOptions.Add(checkMenuOption);
                _api.ModulesOptions.Add(uncheckMenuOption);
            }
        }
        catch (Exception e)
        {
            Logger.LogError("IksAdminApi.dll or MenuManager API not found.");
        }

        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnTick>(OnTick);

        AddCommand("css_contact", Localizer["command_contact_description"], PlayerContactCommand);
        AddCommand("css_close", Localizer["command_close_description"], CloseMessageCommand);

        _chatPrefix = Localizer["chat_prefix"];
    }

    private void PlayerContactCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;

        if (info.ArgCount < 2)
        {
            string errorMessage = Localizer["error_message"];
            player.PrintToChat(_chatPrefix + errorMessage);
            return;
        }

        var discordContact = info.GetArg(1);

        if (player.AuthorizedSteamID == null)
        {
            string errorMessage = Localizer["error_player_no_steamid", player.PlayerName];
            player.PrintToChat(_chatPrefix + errorMessage);
            return;
        }

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_remainingTimes.ContainsKey(playerSteamId64))
        {
            _contactProvided.Add(playerSteamId64);

            if (_adminCheckMessages.TryGetValue(playerSteamId64, out var adminInfo))
            {
                var (playerName, _, adminSteamId64) = adminInfo;
                var admin = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == adminSteamId64);

                if (admin != null)
                {
                    string message = Localizer["admin_message_format", player.PlayerName, discordContact];
                    admin.PrintToChat(_chatPrefix + message);

                    if (Config.EnableDiscordLogging && !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
                        _ = SendDiscordContactProvidedNotification(
                            Config.DiscordWebhookUrl,
                            player.PlayerName,
                            admin.PlayerName,
                            playerSteamId64.ToString(),
                            adminSteamId64.ToString(),
                            discordContact
                        );
                }
            }

            _adminCheckMessages.Remove(playerSteamId64);
            StopCheckTimer(playerSteamId64, false);

            player.PrintToChat(_chatPrefix + Localizer["contact_success_message"]);
        }
        else
        {
            player.PrintToChat(_chatPrefix + Localizer["error_no_active_check"]);
        }
    }


    private void CloseMessageCommand(CCSPlayerController? admin, CommandInfo info)
    {
        if (admin == null || admin.AuthorizedSteamID == null) return;

        var adminSteamId64 = admin.AuthorizedSteamID.SteamId64;
        var hasActiveChecks = _adminCheckMessages.Values.Any(msg => msg.AdminSteamId == adminSteamId64);

        if (hasActiveChecks)
        {
            _hiddenMessagesForAdmins.Add(adminSteamId64);
            admin.PrintToChat(_chatPrefix + Localizer["message_closed"]);
        }
    }

    private void ShowCheckMenu(CCSPlayerController admin)
    {
        if (_menuApi == null || _api == null) return;

        IIksAdminApi.UsedMenuType parsedMenuType;
        if (!Enum.TryParse(_api.Config.MenuType, true, out parsedMenuType))
            parsedMenuType = IIksAdminApi.UsedMenuType.Chat;

        var menuType = parsedMenuType switch
        {
            IIksAdminApi.UsedMenuType.Chat => MenuType.ChatMenu,
            IIksAdminApi.UsedMenuType.Html => MenuType.CenterMenu,
            IIksAdminApi.UsedMenuType.Button => MenuType.ButtonMenu,
            _ => MenuType.ChatMenu
        };

        var checkMenu = _menuApi.NewMenuForcetype(Localizer["select_player_for_check"], menuType);

        foreach (var player in Utilities.GetPlayers())
        {
            if (player.AuthorizedSteamID == null) continue;

            var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

            if (_playersUnderCheck.Contains(playerSteamId64) || _remainingTimes.ContainsKey(playerSteamId64))
            {
                checkMenu.AddMenuOption($"{player.PlayerName} (под проверкой)", null);
                continue;
            }

            checkMenu.AddMenuOption(player.PlayerName, (caller, option) =>
            {
                if (_playersUnderCheck.Contains(playerSteamId64) || _remainingTimes.ContainsKey(playerSteamId64))
                {
                    caller.PrintToChat(_chatPrefix + Localizer["error_already_under_check", player.PlayerName]);
                    Logger.LogInformation(
                        $"Admin {admin.PlayerName} попытался вызвать на проверку игрока {player.PlayerName}, но он уже под проверкой.");
                    return;
                }

                StartCheck(player, admin);
                StartCheckTimer(player, admin);
                _menuApi.CloseMenu(admin);
                caller.PrintToChat(_chatPrefix + Localizer["check", player.PlayerName]);
            });
        }

        checkMenu.Open(admin);
    }

    private void ShowUncheckMenu(CCSPlayerController admin)
    {
        if (_menuApi == null || _api == null) return;

        IIksAdminApi.UsedMenuType parsedMenuType;
        if (!Enum.TryParse(_api.Config.MenuType, true, out parsedMenuType))
            parsedMenuType = IIksAdminApi.UsedMenuType.Chat;

        var menuType = parsedMenuType switch
        {
            IIksAdminApi.UsedMenuType.Chat => MenuType.ChatMenu,
            IIksAdminApi.UsedMenuType.Html => MenuType.CenterMenu,
            IIksAdminApi.UsedMenuType.Button => MenuType.ButtonMenu,
            _ => MenuType.ChatMenu
        };

        var uncheckMenu = _menuApi.NewMenuForcetype(Localizer["select_player_to_uncheck"], menuType);

        foreach (var player in Utilities.GetPlayers())
        {
            if (player.AuthorizedSteamID == null) continue;

            uncheckMenu.AddMenuOption(player.PlayerName, (caller, option) =>
            {
                UncheckPlayer(player, admin);
                _menuApi.CloseMenu(player);
            });
        }

        uncheckMenu.Open(admin);
    }

    private void UncheckPlayer(CCSPlayerController player, CCSPlayerController admin)
    {
        if (player.AuthorizedSteamID == null) return;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (!_remainingTimes.ContainsKey(playerSteamId64) && !_playersUnderCheck.Contains(playerSteamId64))
        {
            admin.PrintToChat(_chatPrefix + Localizer["error_no_active_check_for_admin", player.PlayerName]);
            return;
        }

        StopCheck(playerSteamId64);
        StopCheckTimer(playerSteamId64);

        admin.PrintToChat(_chatPrefix + Localizer["uncheck", player.PlayerName]);

        if (Config.EnableDiscordLogging && !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
            _ = SendDiscordCheckCompletedNotification(
                Config.DiscordWebhookUrl,
                player.PlayerName,
                admin.PlayerName,
                playerSteamId64.ToString(),
                admin.AuthorizedSteamID?.SteamId64.ToString() ?? "Unknown"
            );

        _adminCheckMessages.Remove(playerSteamId64);
        _uncheckMessages[playerSteamId64] = 100;
    }

    private void StartCheckTimer(CCSPlayerController playerToCheck, CCSPlayerController admin)
    {
        if (playerToCheck?.AuthorizedSteamID == null || admin?.AuthorizedSteamID == null)
        {
            admin?.PrintToChat(_chatPrefix +
                               Localizer["error_player_no_steamid", playerToCheck?.PlayerName ?? "Unknown"]);
            return;
        }

        var playerSteamId64 = playerToCheck.AuthorizedSteamID.SteamId64;
        var adminSteamId64 = admin.AuthorizedSteamID.SteamId64;
        var remainingTime = Config.CheckDuration;
        _remainingTimes[playerSteamId64] = remainingTime;

        _adminCheckMessages[playerSteamId64] = (playerToCheck.PlayerName, remainingTime, adminSteamId64);

        if (_playerTimers.TryGetValue(playerSteamId64, out var existingTimer))
        {
            existingTimer.Kill();
            _playerTimers.Remove(playerSteamId64);
        }

        _playerTimers[playerSteamId64] = new Timer(1.0f, () =>
        {
            try
            {
                if (playerToCheck == null || !playerToCheck.IsValid)
                {
                    Logger.LogInformation($"Player {playerSteamId64} is no longer valid. Stopping timer.");
                    StopCheckTimer(playerSteamId64);
                    return;
                }

                var playerName = playerToCheck.PlayerName;
                if (playerName == null)
                {
                    Logger.LogWarning($"PlayerName is null for SteamID: {playerSteamId64}. Stopping timer.");
                    StopCheckTimer(playerSteamId64);
                    return;
                }

                _remainingTimes[playerSteamId64]--;

                if (_remainingTimes[playerSteamId64] > 0)
                    _adminCheckMessages[playerSteamId64] =
                        (playerName, _remainingTimes[playerSteamId64], adminSteamId64);

                if (_remainingTimes[playerSteamId64] <= 0)
                {
                    BanPlayer(playerToCheck, Config.BanReason, Config.BanTime);
                    StopCheckTimer(playerSteamId64);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception in timer callback for player {playerSteamId64}: {ex}");
                StopCheckTimer(playerSteamId64);
            }
        }, TimerFlags.REPEAT);
    }

    private void StopCheckTimer(ulong playerSteamId64, bool removeFromRemainingTimes = true)
    {
        if (_playerTimers.ContainsKey(playerSteamId64))
        {
            _playerTimers[playerSteamId64].Kill();
            _playerTimers.Remove(playerSteamId64);
        }

        if (removeFromRemainingTimes && _remainingTimes.ContainsKey(playerSteamId64))
            _remainingTimes.Remove(playerSteamId64);

        if (removeFromRemainingTimes && _contactProvided.Contains(playerSteamId64))
            _contactProvided.Remove(playerSteamId64);
    }

    private void BanPlayer(CCSPlayerController player, string reason, int time)
    {
        Server.ExecuteCommand($"css_ban #{player.SteamID} {time} \"{reason}\"");
    }

    private void OnTick()
    {
        foreach (var kvp in _adminCheckMessages)
        {
            var playerSteamId64 = kvp.Key;
            var (playerName, timeLeft, adminSteamId64) = kvp.Value;

            if (adminSteamId64 == 0UL || _hiddenMessagesForAdmins.Contains(adminSteamId64)) continue;

            var admin = Utilities.GetPlayers().Find(p =>
                p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == adminSteamId64);
            if (admin != null && timeLeft > 0)
                admin.PrintToCenterHtml(Localizer["html_admin_check_info_message", playerName, timeLeft]);
        }

        foreach (var kvp in new Dictionary<ulong, int>(_uncheckMessages))
        {
            var playerSteamId64 = kvp.Key;
            var displayTime = kvp.Value;

            if (displayTime > 0)
            {
                var player = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                if (player != null) player.PrintToCenterHtml(Localizer["uncheck_message"]);
                _uncheckMessages[playerSteamId64]--;
            }
            else
            {
                _uncheckMessages.Remove(playerSteamId64);
            }
        }

        foreach (var kvp in _remainingTimes)
        {
            var playerSteamId64 = kvp.Key;
            var timeLeft = kvp.Value;

            if (timeLeft > 0)
            {
                string updatedMessage = Localizer["html_countdown_message_format", timeLeft];
                var player = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                if (player != null) player.PrintToCenterHtml(updatedMessage);
            }
        }

        foreach (var kvp in new Dictionary<ulong, int>(_messageDisplayTimes))
        {
            var playerSteamId64 = kvp.Key;
            var displayTime = kvp.Value;

            if (displayTime > 0)
            {
                var player = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                if (player != null) player.PrintToCenterHtml(Localizer["html_success_message"]);
                _messageDisplayTimes[playerSteamId64]--;
            }
            else
            {
                _messageDisplayTimes.Remove(playerSteamId64);
            }
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || player.AuthorizedSteamID == null) return HookResult.Continue;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_remainingTimes.ContainsKey(playerSteamId64))
        {
            var hasProvidedContact = _contactProvided.Contains(playerSteamId64);

            if (Config.BanOnDisconnectAfterContact || (!hasProvidedContact && !Config.BanOnDisconnectAfterContact))
                BanPlayer(player, Config.BanReason, Config.BanTime);
        }

        _adminCheckMessages.Remove(playerSteamId64);
        _playersUnderCheck.Remove(playerSteamId64);
        StopCheckTimer(playerSteamId64);

        return HookResult.Continue;
    }


    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeamPre(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null || player.AuthorizedSteamID == null)
            return HookResult.Continue;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_playersUnderCheck.Contains(playerSteamId64) && Config.BlockTeamChangeDuringCheck)
        {
            Server.NextFrame(() => { player.ChangeTeam(CsTeam.Spectator); });
            player.PrintToChat(_chatPrefix + Localizer["check_team_lock_message"]);
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }


    private void StartCheck(CCSPlayerController playerToCheck, CCSPlayerController admin)
    {
        var playerSteamId64 = playerToCheck.AuthorizedSteamID!.SteamId64;
        var adminSteamId64 = admin.AuthorizedSteamID!.SteamId64;
        _playersUnderCheck.Add(playerSteamId64);

        if (Config.MoveToSpectatorsOnCheck)
            Server.NextFrame(() => { playerToCheck.ChangeTeam(CsTeam.Spectator); });

        playerToCheck.ExecuteClientCommand($"play {Config.CheckSoundPath}");
        playerToCheck.PrintToChat(_chatPrefix + Localizer["check_start_message"]);

        if (Config.EnableDiscordLogging && !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
            _ = SendDiscordCheckStartedNotification(
                Config.DiscordWebhookUrl,
                playerToCheck.PlayerName,
                admin.PlayerName,
                playerSteamId64.ToString(),
                adminSteamId64.ToString()
            );
    }

    private void StopCheck(ulong playerSteamId64)
    {
        if (_playersUnderCheck.Contains(playerSteamId64)) _playersUnderCheck.Remove(playerSteamId64);
    }

    public void OnConfigParsed(IksAdminCheckCheatsConfig config)
    {
        if (config.CheckDuration > 600) config.CheckDuration = 600;
        Config = config;
    }

    private async Task SendDiscordCheckCompletedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64)
    {
        var embed = new
        {
            title = Localizer["discord_check_completed_title"].Value,
            description = Localizer["discord_check_completed_description"].Value,
            color = int.Parse(Config.DiscordColorCheckCompleted.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private async Task SendDiscordContactProvidedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64, string discordContact)
    {
        var embed = new
        {
            title = Localizer["discord_contact_provided_title"].Value,
            description = Localizer["discord_contact_provided_description"].Value,
            color = int.Parse(Config.DiscordColorContactProvided.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_contact_field"].Value,
                    value = discordContact,
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private async Task SendDiscordCheckStartedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64)
    {
        var embed = new
        {
            title = Localizer["discord_check_started_title"].Value,
            description = Localizer["discord_check_started_description"].Value,
            color = int.Parse(Config.DiscordColorCheckStarted.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }
}