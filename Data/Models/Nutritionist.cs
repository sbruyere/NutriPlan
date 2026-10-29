using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

public class Nutritionist
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [Required, MaxLength(200)]
    public string Email { get; set; } = "";

    [Required]
    public string PasswordHash { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Suspendu par l'administration : connexion refusée, données conservées.
    public bool IsSuspended { get; set; }

    public ICollection<FoodDiary> Diaries { get; set; } = [];
}
