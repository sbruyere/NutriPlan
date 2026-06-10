using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class DailyLog
{
    public int Id { get; set; }

    public int FoodDiaryId { get; set; }
    public FoodDiary FoodDiary { get; set; } = null!;

    public DateOnly Date { get; set; }

    [MaxLength(300)]
    public string? Hydration { get; set; }

    [MaxLength(500)]
    public string? Emotions { get; set; }

    [MaxLength(300)]
    public string? PhysicalActivity { get; set; }

    [MaxLength(500)]
    public string? GeneralNotes { get; set; }

    public ICollection<DayEvent> Events { get; set; } = [];
}
