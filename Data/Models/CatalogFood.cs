using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

// Référentiel d'aliments génériques importé de la table Ciqual 2025 (ANSES,
// Licence Ouverte Etalab 2.0 — attribution affichée dans le pied de page),
// enrichi hors ligne des unités de mesure naturelles, du poids par unité et
// de la portion typique adulte. Seed : Data/Seed/food-catalog.json.
public class CatalogFood
{
    public int Id { get; set; }                                   // alim_code Ciqual
    [Required, MaxLength(255)] public string Name { get; set; } = "";
    [Required, MaxLength(255)] public string SearchName { get; set; } = ""; // minuscules sans accents
    [MaxLength(2)] public string GroupCode { get; set; } = "";
    [MaxLength(4)] public string SubGroupCode { get; set; } = "";
    [MaxLength(120)] public string? SubGroupName { get; set; }
    public double? KcalPer100g { get; set; }                      // Énergie, Règlement UE 1169/2011
    // Macronutriments Ciqual pour 100 g (protéines N×6,25 — norme étiquetage)
    public double? ProteinPer100g { get; set; }
    public double? CarbsPer100g { get; set; }
    public double? SugarsPer100g { get; set; }
    public double? FatPer100g { get; set; }
    public double? SatFatPer100g { get; set; }
    public double? FiberPer100g { get; set; }
    public double? SaltPer100g { get; set; }
    [Required, MaxLength(20)] public string DefaultUnit { get; set; } = "g";
    [Required, MaxLength(120)] public string Units { get; set; } = "g"; // unités pertinentes, séparées par |
    public double? UnitWeightG { get; set; }                      // poids (g) d'UNE unité par défaut non métrique
    public double? TypicalQty { get; set; }                       // portion typique adulte, exprimée dans DefaultUnit
    // Référentiel d'origine : "ciqual" (ANSES 2025) ou "usda" (FoodData Central,
    // complément traduit en français pour les aliments absents de Ciqual).
    [Required, MaxLength(10)] public string Source { get; set; } = "ciqual";
}
