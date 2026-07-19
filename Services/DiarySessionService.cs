using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace NutriPlan.Services;

/// <summary>
/// Service scopé par circuit Blazor — conserve les carnets déverrouillés et la session
/// nutritionniste, avec persistance chiffrée côté navigateur (ProtectedLocalStorage)
/// pour survivre aux rafraîchissements de page et aux coupures SignalR.
/// Chaque déverrouillage est lié à une empreinte de version du PIN : changer ou définir
/// le PIN invalide automatiquement les déverrouillages persistés sur les autres appareils.
/// </summary>
public class DiarySessionService(ProtectedLocalStorage storage)
{
    private const string StorageKey = "nutriplan-session";

    private readonly Dictionary<string, string> _unlockedCodes = []; // code → empreinte du PIN au déverrouillage
    private int? _nutritionistId;
    private string? _nutritionistName;
    private Task? _loadTask;

    private sealed record SessionState(Dictionary<string, string> UnlockedCodes, int? NutritionistId, string? NutritionistName);

    /// <summary>Empreinte de version du PIN : change dès que le PIN est défini, modifié ou supprimé.</summary>
    public static string PinStamp(string? pinHash) =>
        pinHash == null ? "open" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pinHash)))[..16];

    /// <summary>
    /// Recharge la session depuis le navigateur (une seule fois par circuit).
    /// À appeler avant toute lecture de IsUnlocked / IsNutritionistLoggedIn.
    /// Nécessite le JS interop — donc jamais pendant un prerender.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<SessionState>(StorageKey);
            if (result.Success && result.Value is { } state)
            {
                foreach (var kv in state.UnlockedCodes) _unlockedCodes[kv.Key] = kv.Value;
                _nutritionistId = state.NutritionistId;
                _nutritionistName = state.NutritionistName;
            }
        }
        catch
        {
            // Stockage indisponible, format obsolète ou clés de chiffrement changées — session vierge.
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await storage.SetAsync(StorageKey, new SessionState(new Dictionary<string, string>(_unlockedCodes), _nutritionistId, _nutritionistName));
        }
        catch
        {
            // Circuit déconnecté ou stockage refusé — la session reste valide en mémoire.
        }
    }

    /// <summary>Le carnet est-il déverrouillé pour la version de PIN actuelle ?</summary>
    public bool IsUnlocked(string accessCode, string pinStamp) =>
        _unlockedCodes.TryGetValue(accessCode, out var stamp) && stamp == pinStamp;

    public async Task UnlockAsync(string accessCode, string pinStamp)
    {
        if (_unlockedCodes.TryGetValue(accessCode, out var existing) && existing == pinStamp) return;
        _unlockedCodes[accessCode] = pinStamp;
        await PersistAsync();
    }

    public bool IsNutritionistLoggedIn => _nutritionistId.HasValue;
    public int? NutritionistId => _nutritionistId;
    public string? NutritionistName => _nutritionistName;

    public async Task LoginNutritionistAsync(int id, string name)
    {
        _nutritionistId = id;
        _nutritionistName = name;
        await PersistAsync();
        NotifyStateChanged();
    }

    public async Task LogoutNutritionistAsync()
    {
        _nutritionistId = null;
        _nutritionistName = null;
        await PersistAsync();
        NotifyStateChanged();
    }

    public event Action? OnChange;
    public void NotifyStateChanged() => OnChange?.Invoke();
}
