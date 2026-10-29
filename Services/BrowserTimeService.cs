using Microsoft.JSInterop;

namespace NutriPlan.Services;

/// <summary>
/// Fournit la date « aujourd'hui » du navigateur (fuseau horaire du client),
/// pour que le journal ne bascule pas de jour à l'heure du serveur (UTC en prod).
/// Nécessite le JS interop — initialiser hors prerender.
/// </summary>
public class BrowserTimeService(IJSRuntime js)
{
    private TimeZoneInfo? _tz;
    private Task? _initTask;

    public Task InitializeAsync() => _initTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var tzName = await js.InvokeAsync<string>("nutriplan.timeZone");
            _tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
        }
        catch
        {
            try
            {
                // Repli : décalage fixe (ne suit pas les changements d'heure en cours de session).
                var offsetMinutes = await js.InvokeAsync<int>("nutriplan.tzOffsetMinutes");
                _tz = TimeZoneInfo.CreateCustomTimeZone("client-fixed", TimeSpan.FromMinutes(-offsetMinutes), "Client", "Client");
            }
            catch
            {
                _tz = null; // Dernier repli : heure du serveur.
            }
        }
    }

    /// <summary>Date du jour côté client (repli : date du serveur si le fuseau est inconnu).</summary>
    public DateOnly Today => _tz == null
        ? DateOnly.FromDateTime(DateTime.Today)
        : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz));

    public async Task<DateOnly> TodayAsync()
    {
        await InitializeAsync();
        return Today;
    }

    /// <summary>Heure courante côté client (repli : heure du serveur) — ex. pré-choix du créneau de repas.</summary>
    public TimeOnly TimeNow => _tz == null
        ? TimeOnly.FromDateTime(DateTime.Now)
        : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz));
}
