using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class MealPhoto
{
    public int Id { get; set; }
    public int DayEventId { get; set; }
    public DayEvent DayEvent { get; set; } = null!;
    [Required, MaxLength(300)] public string FilePath { get; set; } = "";
    // Position d'affichage réorganisable ; existant à 0 → départage par CreatedAt
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
