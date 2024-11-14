using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace IksAdminCheckCheatsPlugin;

public class IksAdminCheckCheatsConfig : BasePluginConfig
{
    [JsonPropertyName("ban_reason")] public string BanReason { get; set; } = "Cheats";

    [JsonPropertyName("ban_time")] public int BanTime { get; set; } = 0;

    [JsonPropertyName("check_duration")] public int CheckDuration { get; set; } = 120;

    [JsonPropertyName("flag")] public string Flag { get; set; } = "c";

    [JsonPropertyName("check_sound_path")] public string CheckSoundPath { get; set; } = "sounds/buttons/button8.vsnd_c";
    [JsonPropertyName("overlay")] public bool Overlay { get; set; } = false;
    [JsonPropertyName("overlay_path")] public string OverlayPath { get; set; } = "particles/cheats_check.vpcf";
    [JsonPropertyName("show_html_message_suspect")] public bool ShowHtmlMessageSuspect { get; set; } = true;    
    [JsonPropertyName("ban_on_disconnect_after_contact")]
    public bool BanOnDisconnectAfterContact { get; set; } = true;

    [JsonPropertyName("move_to_spectators_on_check")]
    public bool MoveToSpectatorsOnCheck { get; set; } = true;

    [JsonPropertyName("block_team_change_during_check")]
    public bool BlockTeamChangeDuringCheck { get; set; } = true;

    [JsonPropertyName("enable_discord_logging")]
    public bool EnableDiscordLogging { get; set; } = true;
    [JsonPropertyName("webhook_mode")] public int WebhookMode { get; set; } = 1;

    [JsonPropertyName("discord_webhook_url")]
    public string DiscordWebhookUrl { get; set; } = "";

    [JsonPropertyName("discord_color_check_started")]
    public string DiscordColorCheckStarted { get; set; } = "FFA500";

    [JsonPropertyName("discord_color_contact_provided")]
    public string DiscordColorContactProvided { get; set; } = "00FF00";

    [JsonPropertyName("discord_color_check_completed")]
    public string DiscordColorCheckCompleted { get; set; } = "00FF00";

    [JsonPropertyName("discord_footer_icon_url")]
    public string DiscordFooterIconUrl { get; set; } = "https://i.imgur.com/2NbqQu7.png";
    
    [JsonPropertyName("server_id")]
    public int ServerId { get; set; } = 1; 
   
    [JsonPropertyName("enable_database_logging")]
    public bool EnableDatabaseLogging { get; set; } = false;
    
    [JsonPropertyName("database_host")]
    public string DatabaseHost { get; set; } = "localhost";

    [JsonPropertyName("database_user")]
    public string DatabaseUser { get; set; } = "root";        
    
    [JsonPropertyName("database_name")]
    public string DatabaseName { get; set; } = "checkcheats_db";       
    
    [JsonPropertyName("database_password")]
    public string DatabasePassword { get; set; } = "";
    
    [JsonPropertyName("database_port")]
    public int DatabasePort { get; set; } = 3306;

    [JsonPropertyName("table_name")]
    public string TableName { get; set; } = "checkcheats_stats";
    
}