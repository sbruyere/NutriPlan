namespace NutriPlan.Services;

/// <summary>
/// Service scopé par circuit Blazor — conserve les carnets déverrouillés dans la session courante.
/// </summary>
public class DiarySessionService
{
    private readonly HashSet<string> _unlockedCodes = [];
    private int? _nutritionistId;
    private string? _nutritionistName;

    public bool IsUnlocked(string accessCode) => _unlockedCodes.Contains(accessCode);
    public void Unlock(string accessCode) => _unlockedCodes.Add(accessCode);

    public bool IsNutritionistLoggedIn => _nutritionistId.HasValue;
    public int? NutritionistId => _nutritionistId;
    public string? NutritionistName => _nutritionistName;

    public void LoginNutritionist(int id, string name)
    {
        _nutritionistId = id;
        _nutritionistName = name;
    }

    public void LogoutNutritionist()
    {
        _nutritionistId = null;
        _nutritionistName = null;
    }

    public event Action? OnChange;
    public void NotifyStateChanged() => OnChange?.Invoke();
}
