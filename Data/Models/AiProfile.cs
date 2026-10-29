using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

// Clé d'API IA enregistrée pour la saisie express : plusieurs profils peuvent
// coexister (fournisseurs ou modèles différents), un seul est actif à la fois
// (pointeur AppSettings « ai.activeProfile »). Révoquer = supprimer la ligne.
public class AiProfile
{
    public int Id { get; set; }
    [Required, MaxLength(30)] public string Provider { get; set; } = "";   // anthropic | openai | azure | openrouter
    [Required, MaxLength(100)] public string Model { get; set; } = "";     // modèle, ou nom de déploiement Azure
    [MaxLength(300)] public string Endpoint { get; set; } = "";            // ressource Azure uniquement
    [Required, MaxLength(300)] public string ApiKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
