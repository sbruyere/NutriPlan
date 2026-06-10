using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class DrinkEntry
{
    public int Id { get; set; }
    public int DayEventId { get; set; }
    public DayEvent DayEvent { get; set; } = null!;
    [Required, MaxLength(100)] public string DrinkName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
