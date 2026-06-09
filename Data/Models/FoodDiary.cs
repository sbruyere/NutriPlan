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

    public int? NutritionistId { get; set; }
    public Nutritionist? Nutritionist { get; set; }

    public ICollection<DailyLog> DailyLogs { get; set; } = [];
}
