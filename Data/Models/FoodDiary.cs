using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class FoodDiary
{
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string AccessCode { get; set; } = "";

    public string? PinHash { get; set; }

    [MaxLength(100)]
    public string? PatientAlias { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsNutritionistManaged { get; set; }

    // ── Profil (facultatif) : métabolisme de base Mifflin-St Jeor + objectifs ──
    [MaxLength(1)] public string? Sex { get; set; }            // "F" / "M"
    public int? HeightCm { get; set; }
    public double? WeightKg { get; set; }
    public int? BirthYear { get; set; }
    [MaxLength(30)] public string? ActivityLevel { get; set; } // clé DiaryService.ActivityLevels
    public int? DailyKcalTarget { get; set; }                  // plafond d'apport journalier (kcal)
    public int? DailyBurnTarget { get; set; }                  // objectif de dépense sportive (kcal)

    public int? NutritionistId { get; set; }
    public Nutritionist? Nutritionist { get; set; }

    public ICollection<DailyLog> DailyLogs { get; set; } = [];
}
