using System.ComponentModel.DataAnnotations;

namespace NutriPlan.Data.Models;

// Commentaire du nutritionniste sur une journée du carnet, visible par le
// patient (bandeau dédié dans le journal). ReadByPatient pilote le badge
// « Nouveau » et la notification à l'ouverture du carnet.
public class DayComment
{
    public int Id { get; set; }

    public int DailyLogId { get; set; }
    public DailyLog DailyLog { get; set; } = null!;

    [Required, MaxLength(1000)]
    public string Text { get; set; } = "";

    [MaxLength(100)]
    public string? AuthorName { get; set; }

    public bool ReadByPatient { get; set; }

    // Réaction rapide du patient (« 👍 » / « Merci ! ») — boucle de retour légère.
    [MaxLength(20)]
    public string? PatientReaction { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
