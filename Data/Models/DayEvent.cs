using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public enum DayEventType { Meal = 0, Activity = 1 }

public class DayEvent
{
    public int Id { get; set; }
    public int DailyLogId { get; set; }
    public DailyLog DailyLog { get; set; } = null!;
    public DayEventType EventType { get; set; }
    public TimeOnly? Time { get; set; }
    [Required, MaxLength(100)] public string Label { get; set; } = "";
    // Meal
    public int? HungerBefore { get; set; }
    public int? SatietyAfter { get; set; }
    [MaxLength(500)] public string? Sensations { get; set; }
    [MaxLength(500)] public string? Emotions { get; set; }
    // Activity
    public int? DurationMinutes { get; set; }
    [MaxLength(50)] public string? Intensity { get; set; }
    public int? CaloriesBurned { get; set; }        // kcal dépensées pendant l'activité (saisie utilisateur)
    public double? DistanceKm { get; set; }         // distance parcourue (optionnel)
    public int? ElevationM { get; set; }            // dénivelé positif en mètres (optionnel)
    public long? StravaActivityId { get; set; }     // activité importée de Strava (anti-doublon)
    [MaxLength(500)] public string? ActivityNotes { get; set; }
    public int? MealSlot { get; set; }               // 0-5 pour les jalons prédéfinis, null sinon
    public int? ActivityAfterSlot { get; set; }      // activité entre jalons : après le jalon N (null = avant le 1er)
    public int? ConcurrentWithMealSlot { get; set; } // activité simultanée à un repas prédéfini
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<FoodEntry> FoodEntries { get; set; } = [];
    public ICollection<DrinkEntry> DrinkEntries { get; set; } = [];
    public ICollection<MealPhoto> Photos { get; set; } = [];
}
