namespace NutriPlan.Data.Models;

// Historique pondéral : une pesée par jour et par carnet (la dernière saisie du
// jour remplace la précédente). FoodDiary.WeightKg reste le poids « courant »
// (profil, Mifflin-St Jeor) et est synchronisé à chaque nouvelle pesée.
public class WeightEntry
{
    public int Id { get; set; }

    public int FoodDiaryId { get; set; }
    public FoodDiary FoodDiary { get; set; } = null!;

    public DateOnly Date { get; set; }
    public double WeightKg { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
