using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public enum MealType
{
    PetitDejeuner,
    Dejeuner,
    Diner,
    Collation,
    Autre
}

public class FoodEntry
{
    public int Id { get; set; }

    public int DailyLogId { get; set; }
    public DailyLog DailyLog { get; set; } = null!;

    [Required, MaxLength(200)]
    public string FoodName { get; set; } = "";

    [MaxLength(100)]
    public string? Quantity { get; set; }

    public MealType MealType { get; set; }

    public TimeOnly? Time { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
