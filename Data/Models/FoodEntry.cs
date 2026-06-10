using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class FoodEntry
{
    public int Id { get; set; }
    public int DayEventId { get; set; }
    public DayEvent DayEvent { get; set; } = null!;
    [Required, MaxLength(200)] public string FoodName { get; set; } = "";
    [MaxLength(100)] public string? Quantity { get; set; }
    [MaxLength(200)] public string? PreparationMethod { get; set; }
    [MaxLength(100)] public string? GroupName { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
