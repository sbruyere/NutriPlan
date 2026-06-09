using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class MealLog
{
    public int Id { get; set; }

    public int DailyLogId { get; set; }
    public DailyLog DailyLog { get; set; } = null!;

    public MealType MealType { get; set; }

    public int? HungerBefore { get; set; }

    public int? SatietyAfter { get; set; }

    [MaxLength(500)]
    public string? BetweenMealSensations { get; set; }

    public int SortOrder { get; set; }

    public ICollection<FoodEntry> FoodEntries { get; set; } = [];
}
