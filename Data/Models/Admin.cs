using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

// Compte administrateur de la plateforme. Le premier compte est créé par
// l'écran de configuration initiale (/admin/installation), accessible
// uniquement tant qu'aucun administrateur n'existe.
public class Admin
{
    public int Id { get; set; }

    [Required, MaxLength(255)]
    public string Email { get; set; } = "";

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [Required]
    public string PasswordHash { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
