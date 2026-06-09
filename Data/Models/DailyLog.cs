namespace NutriPlan.Data.Models;

public class DailyLog
{
    public int Id { get; set; }

    public int FoodDiaryId { get; set; }
    public FoodDiary FoodDiary { get; set; } = null!;

    public DateOnly Date { get; set; }

    public string? Notes { get; set; }

    public ICollection<FoodEntry> FoodEntries { get; set; } = [];
}
