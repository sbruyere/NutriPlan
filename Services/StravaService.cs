using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

// Intégration Strava : OAuth2 par carnet + import des activités (titre, durée,
// distance, dénivelé, kcal) en événements Activité de la frise.
// Nécessite la configuration Strava:ClientId / Strava:ClientSecret
// (application à créer sur https://www.strava.com/settings/api).
public class StravaService(
    IDbContextFactory<NutriPlanContext> factory,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    DiaryService diaryService)
{
    const string TokenUrl = "https://www.strava.com/oauth/token";
    const string ApiBase = "https://www.strava.com/api/v3";
    const int MaxDetailCallsPerSync = 30; // quotas API : le détail (kcal) est limité par synchro

    string? ClientId => config["Strava:ClientId"];
    string? ClientSecret => config["Strava:ClientSecret"];

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    public string GetAuthorizeUrl(string redirectUri, string accessCode) =>
        "https://www.strava.com/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(ClientId ?? "")}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&approval_prompt=auto" +
        "&scope=activity:read_all" +
        $"&state={Uri.EscapeDataString(accessCode)}";

    // ── DTO Strava (champs snake_case) ───────────────────────────────────────

    record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_at")] long ExpiresAt,
        [property: JsonPropertyName("athlete")] AthleteDto? Athlete);

    record AthleteDto([property: JsonPropertyName("id")] long Id);

    record ActivityDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("distance")] double DistanceMeters,
        [property: JsonPropertyName("moving_time")] int MovingTimeSeconds,
        [property: JsonPropertyName("total_elevation_gain")] double ElevationGain,
        [property: JsonPropertyName("sport_type")] string? SportType,
        [property: JsonPropertyName("start_date_local")] DateTime StartDateLocal,
        [property: JsonPropertyName("calories")] double? Calories,
        [property: JsonPropertyName("photos")] PhotoSummaryDto? Photos);

    record PhotoSummaryDto([property: JsonPropertyName("primary")] PrimaryPhotoDto? Primary);

    record PrimaryPhotoDto([property: JsonPropertyName("urls")] Dictionary<string, string>? Urls);

    // ── Connexion / déconnexion ──────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string accessCode, string code)
    {
        if (!IsConfigured) return false;
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = ClientSecret!,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        });
        if (token == null) return false;

        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FirstOrDefaultAsync(d => d.AccessCode == accessCode);
        if (diary == null) return false;
        diary.StravaAthleteId = token.Athlete?.Id;
        diary.StravaAccessToken = token.AccessToken;
        diary.StravaRefreshToken = token.RefreshToken;
        diary.StravaTokenExpiresAt = token.ExpiresAt;
        await ctx.SaveChangesAsync();
        return true;
    }

    // Supprime toutes les activités importées de Strava (et leurs photos), et remet
    // la synchro à zéro : un « Synchroniser » ré-importe les 30 derniers jours.
    // Pour une suppression définitive, déconnecter le compte d'abord.
    public async Task<int> DeleteImportedActivitiesAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var events = await ctx.DayEvents
            .Include(e => e.Photos)
            .Where(e => e.StravaActivityId != null && e.DailyLog.FoodDiaryId == diaryId)
            .ToListAsync();
        foreach (var photo in events.SelectMany(e => e.Photos))
        {
            var path = Path.Combine(AppPaths.UploadsDir, Path.GetFileName(photo.FilePath));
            try { if (File.Exists(path)) File.Delete(path); } catch { /* fichier déjà absent */ }
        }
        ctx.DayEvents.RemoveRange(events); // les photos suivent en cascade
        if (await ctx.FoodDiaries.FindAsync(diaryId) is { } diary)
            diary.StravaLastSyncAt = null;
        await ctx.SaveChangesAsync();
        return events.Count;
    }

    public async Task DisconnectAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null) return;
        diary.StravaAthleteId = null;
        diary.StravaAccessToken = null;
        diary.StravaRefreshToken = null;
        diary.StravaTokenExpiresAt = null;
        diary.StravaLastSyncAt = null;
        await ctx.SaveChangesAsync();
    }

    // ── Synchronisation des activités ────────────────────────────────────────

    // Une seule synchro à la fois par carnet : l'auto-sync à l'ouverture et le
    // bouton manuel (ou deux onglets) peuvent se chevaucher — sans verrou, les
    // deux passes liraient les mêmes « déjà importés » et créeraient des doublons.
    static readonly HashSet<int> syncing = [];

    // Importe les activités depuis la dernière synchro (fenêtre de rattrapage
    // de 24 h, 30 jours max au premier import). Anti-doublon par StravaActivityId.
    public async Task<(int Imported, string? Error)> SyncAsync(int diaryId)
    {
        lock (syncing)
        {
            if (syncing.Contains(diaryId)) return (0, null); // déjà en cours : ne rien relancer
            syncing.Add(diaryId);
        }
        try
        {
            return await SyncCoreAsync(diaryId);
        }
        finally
        {
            lock (syncing) { syncing.Remove(diaryId); }
        }
    }

    async Task<(int Imported, string? Error)> SyncCoreAsync(int diaryId)
    {
        if (!IsConfigured) return (0, "Strava n'est pas configuré côté serveur.");
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null || string.IsNullOrEmpty(diary.StravaRefreshToken))
            return (0, "Strava n'est pas connecté à ce carnet.");

        var access = await GetValidAccessTokenAsync(diary, ctx);
        if (access == null) return (0, "Connexion Strava expirée — reconnectez le compte.");

        var after = (diary.StravaLastSyncAt is DateTime last
                ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)).AddDays(-1)
                : DateTimeOffset.UtcNow.AddDays(-30))
            .ToUnixTimeSeconds();

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);

        List<ActivityDto>? activities;
        try
        {
            activities = await http.GetFromJsonAsync<List<ActivityDto>>(
                $"{ApiBase}/athlete/activities?after={after}&per_page=100");
        }
        catch (Exception)
        {
            return (0, "Strava est injoignable — réessayez plus tard.");
        }
        if (activities == null) return (0, "Réponse Strava invalide.");

        var knownIds = await ctx.DayEvents
            .Where(e => e.StravaActivityId != null && e.DailyLog.FoodDiaryId == diaryId)
            .Select(e => e.StravaActivityId!.Value)
            .ToListAsync();

        var imported = 0;
        foreach (var act in activities.Where(a => !knownIds.Contains(a.Id)).OrderBy(a => a.StartDateLocal))
        {
            // Défense en profondeur (multi-instance, listes périmées) : re-vérifier en base.
            if (await ctx.DayEvents.AnyAsync(e => e.StravaActivityId == act.Id && e.DailyLog.FoodDiaryId == diaryId))
                continue;

            var date = DateOnly.FromDateTime(act.StartDateLocal);
            var log = await diaryService.GetOrCreateDailyLogAsync(diaryId, date);

            // kcal et photo principale ne sont que dans le détail (appel par activité, plafonné).
            double? calories = null;
            string? photoUrl = null;
            if (imported < MaxDetailCallsPerSync)
            {
                try
                {
                    var detail = await http.GetFromJsonAsync<ActivityDto>($"{ApiBase}/activities/{act.Id}");
                    calories = detail?.Calories;
                    photoUrl = detail?.Photos?.Primary?.Urls?
                        .OrderByDescending(u => int.TryParse(u.Key, out var size) ? size : 0)
                        .Select(u => u.Value)
                        .FirstOrDefault();
                }
                catch { /* kcal/photo facultatives : l'import continue sans */ }
            }

            var evt = new DayEvent
            {
                DailyLogId = log.Id,
                EventType = DayEventType.Activity,
                Time = TimeOnly.FromDateTime(act.StartDateLocal),
                Label = string.IsNullOrWhiteSpace(act.Name) ? SportLabel(act.SportType) : act.Name!.Trim(),
                DurationMinutes = act.MovingTimeSeconds > 0 ? (int)Math.Round(act.MovingTimeSeconds / 60.0) : null,
                DistanceKm = act.DistanceMeters > 0 ? Math.Round(act.DistanceMeters / 1000.0, 2) : null,
                ElevationM = act.ElevationGain > 0 ? (int)Math.Round(act.ElevationGain) : null,
                CaloriesBurned = calories is double kcal && kcal > 0 ? (int)Math.Round(kcal) : null,
                StravaActivityId = act.Id,
                CreatedAt = DateTime.UtcNow,
            };
            ctx.DayEvents.Add(evt);
            await ctx.SaveChangesAsync(); // Id nécessaire pour rattacher la photo

            if (photoUrl != null)
            {
                try
                {
                    // Le CDN Strava n'exige pas d'authentification : client nu.
                    var bytes = await httpFactory.CreateClient().GetByteArrayAsync(photoUrl);
                    using var ms = new MemoryStream(bytes);
                    await diaryService.AddPhotoAsync(evt.Id, ms, "strava.jpg");
                }
                catch { /* photo facultative */ }
            }
            imported++;
        }

        diary.StravaLastSyncAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return (imported, null);
    }

    // ── Interne ──────────────────────────────────────────────────────────────

    async Task<TokenResponse?> RequestTokenAsync(Dictionary<string, string> form)
    {
        try
        {
            var http = httpFactory.CreateClient();
            var resp = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
            if (!resp.IsSuccessStatusCode)
            {
                // Le corps de l'erreur Strava dit précisément ce qui cloche
                // (invalid client_secret, code déjà consommé…) : indispensable au diagnostic.
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"Strava token {form.GetValueOrDefault("grant_type")} : {(int)resp.StatusCode} {body}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<TokenResponse>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Strava token : exception {ex.Message}");
            return null;
        }
    }

    async Task<string?> GetValidAccessTokenAsync(FoodDiary diary, NutriPlanContext ctx)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(diary.StravaAccessToken) &&
            diary.StravaTokenExpiresAt is long exp && exp - 300 > now)
            return diary.StravaAccessToken;

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = ClientSecret!,
            ["refresh_token"] = diary.StravaRefreshToken!,
            ["grant_type"] = "refresh_token",
        });
        if (token == null) return null;
        diary.StravaAccessToken = token.AccessToken;
        diary.StravaRefreshToken = token.RefreshToken;
        diary.StravaTokenExpiresAt = token.ExpiresAt;
        await ctx.SaveChangesAsync();
        return token.AccessToken;
    }

    static string SportLabel(string? sportType) => sportType switch
    {
        "Run" or "TrailRun" or "VirtualRun" => "Course à pied",
        "Ride" or "VirtualRide" or "GravelRide" or "MountainBikeRide" or "EBikeRide" => "Vélo",
        "Swim" => "Natation",
        "Hike" => "Randonnée",
        "Walk" => "Marche",
        "WeightTraining" => "Musculation",
        "Yoga" => "Yoga",
        null or "" => "Activité",
        _ => sportType,
    };
}
