using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class FoodEntry
{
    public int Id { get; set; }
    public int DayEventId { get; set; }
    public DayEvent DayEvent { get; set; } = null!;
    [Required, MaxLength(200)] public string FoodName { get; set; } = "";
    [MaxLength(100)] public string? Quantity { get; set; }     // valeur numérique (ex: "150")
    [MaxLength(50)]  public string? QuantityUnit { get; set; } // unité (ex: "g", "pièce(s)")
    [MaxLength(200)] public string? PreparationMethod { get; set; }
    [MaxLength(100)] public string? GroupName { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    // Lien vers le référentiel Ciqual (CatalogFood) quand l'aliment vient de l'autocomplétion
    public int? CatalogFoodId { get; set; }
    // Position d'affichage réorganisable ; existant à 0 → départage par CreatedAt
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
