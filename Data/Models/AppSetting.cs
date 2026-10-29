using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

// Réglages de plateforme clé → valeur (fournisseur IA, etc.), gérés par l'admin.
public class AppSetting
{
    [Key, MaxLength(100)] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
