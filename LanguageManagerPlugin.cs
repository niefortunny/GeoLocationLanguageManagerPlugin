using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MaxMind.GeoIP2;
using MySqlConnector;

namespace LanguageManagerPlugin;

public class LanguageManagerConfig : BasePluginConfig
{
    public string DatabaseHost { get; set; } = "127.0.0.1";
    public int DatabasePort { get; set; } = 3306;
    public string DatabaseUser { get; set; } = "root";
    public string DatabasePassword { get; set; } = "";
    public string DatabaseName { get; set; } = "language_manager";
}

public class LanguageManagerPlugin : BasePlugin, IPluginConfig<LanguageManagerConfig>
{
    public override string ModuleAuthor => "aproxje";
    public override string ModuleName => "Language Manager Plugin";
    public override string ModuleVersion => "0.0.2";

    public LanguageManagerConfig Config { get; set; } = new();

    private readonly PlayerLanguageManager _playerLanguageManager = new();

    private string? _connectionString;
    private Task _databaseSetup = Task.CompletedTask;
    private bool _databaseReady;
    private DatabaseReader? _geoReader;

    private static readonly Dictionary<string, CultureInfo> CulturesByRegion = BuildRegionCultures();

    public void OnConfigParsed(LanguageManagerConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _connectionString =
            $"Server={Config.DatabaseHost};Port={Config.DatabasePort};User ID={Config.DatabaseUser};Password={Config.DatabasePassword};Database={Config.DatabaseName};";
        _databaseSetup = SetupPlayerDatabaseAsync();
        SetupGeoReader();
        AddCommand("css_lang", "Language manager hook", OnLangCommand);
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
    }

    public override void Unload(bool hotReload)
    {
        _geoReader?.Dispose();
    }

    private void OnClientAuthorized(int slot, SteamID steamId)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || player.IsBot) return;

        var playerIp = GetPlayerIp(player);
        var steamId64 = steamId.SteamId64;

        _ = ApplyLanguageAsync(playerIp, steamId64);
    }

    private async Task ApplyLanguageAsync(string? playerIp, ulong steamId64)
    {
        try
        {
            var stored = await GetStoredCultureAsync(steamId64);
            var fromGeo = stored == null;
            var cultureName = stored ?? CountryCultureName(playerIp);
            if (cultureName == null || !TryResolveCulture(cultureName, out var culture)) return;

            Server.NextFrame(() => _playerLanguageManager.SetLanguage(new SteamID(steamId64), culture));

            if (fromGeo)
                await SaveCultureAsync(steamId64, culture.Name);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }

    private void OnLangCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || info.ArgCount < 2) return;

        var isoCode = info.GetArg(1);
        if (isoCode.Length != 2 || !TryResolveCulture(isoCode, out var culture)) return;

        var steamId64 = player.SteamID;
        _playerLanguageManager.SetLanguage(new SteamID(steamId64), culture);
        _ = SaveCultureAsync(steamId64, culture.Name);
    }

    private async Task SetupPlayerDatabaseAsync()
    {
        try
        {
            await using var connection = await OpenConnectionAsync();
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS CSSPlayerLanguages (
                    steamID BIGINT UNSIGNED NOT NULL PRIMARY KEY,
                    playerISOCode VARCHAR(32) NOT NULL)
                """);
            _databaseReady = true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"MySQL setup failed: {e.Message}");
        }
    }

    private async Task<MySqlConnection> OpenConnectionAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private void SetupGeoReader()
    {
        var path = Path.Combine(ModuleDirectory, "GeoLite2-Country.mmdb");
        if (!File.Exists(path))
        {
            Console.WriteLine($"GeoLite2-Country.mmdb not found at {path}");
            return;
        }

        _geoReader = new DatabaseReader(path);
    }

    private async Task<string?> GetStoredCultureAsync(ulong steamId)
    {
        await _databaseSetup;
        if (!_databaseReady) return null;

        await using var connection = await OpenConnectionAsync();
        return await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT playerISOCode FROM CSSPlayerLanguages WHERE steamID = @SteamID",
            new { SteamID = steamId });
    }

    private async Task SaveCultureAsync(ulong steamId, string cultureName)
    {
        try
        {
            await _databaseSetup;
            if (!_databaseReady) return;

            await using var connection = await OpenConnectionAsync();
            await connection.ExecuteAsync("""
                INSERT INTO CSSPlayerLanguages (steamID, playerISOCode) VALUES (@SteamID, @PlayerISO)
                ON DUPLICATE KEY UPDATE playerISOCode = @PlayerISO
                """, new { SteamID = steamId, PlayerISO = cultureName });
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }

    private string? CountryCultureName(string? ipAddress)
    {
        if (ipAddress == null || _geoReader == null) return null;

        try
        {
            var country = _geoReader.Country(ipAddress).Country.IsoCode;
            return country != null && CulturesByRegion.TryGetValue(country, out var culture)
                ? culture.Name
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return null;
        }
    }

    private static string? GetPlayerIp(CCSPlayerController player)
    {
        var playerIp = player.IpAddress;
        if (string.IsNullOrEmpty(playerIp)) return null;

        if (playerIp[0] == '[')
        {
            var end = playerIp.IndexOf(']');
            if (end > 1) return playerIp[1..end];
        }

        var colon = playerIp.LastIndexOf(':');
        return colon > 0 ? playerIp[..colon] : playerIp;
    }

    private static bool TryResolveCulture(string code, out CultureInfo culture)
    {
        try
        {
            culture = CultureInfo.GetCultureInfo(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            if (CulturesByRegion.TryGetValue(code, out culture!)) return true;
            culture = CultureInfo.InvariantCulture;
            return false;
        }
    }

    private static Dictionary<string, CultureInfo> BuildRegionCultures()
    {
        var map = new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                map.TryAdd(new RegionInfo(culture.Name).TwoLetterISORegionName, culture);
            }
            catch (ArgumentException)
            {
            }
        }

        return map;
    }
}
