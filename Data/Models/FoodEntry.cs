using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public enum MealType
{
    PetitDejeuner = 0,
    CollationMatin = 1,
    Dejeuner = 2,
    CollationApresMidi = 3,
    Diner = 4,
    CollationSoir = 5
}

public class FoodEntry
{
    public int Id { get; set; }

    public int MealLogId { get; set; }
    public MealLog MealLog { get; set; } = null!;

    [Required, MaxLength(200)]
    public string FoodName { get; set; } = "";

    [MaxLength(100)]
    public string? Quantity { get; set; }

    [MaxLength(200)]
    public string? PreparationMethod { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
