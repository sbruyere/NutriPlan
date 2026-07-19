namespace NutriPlan.Services;

// Chemins de stockage persistant, résolus au démarrage (Program.cs).
// Sur Azure App Service, le dossier de déploiement est REMPLACÉ à chaque
// publication : la base et les photos doivent vivre dans %HOME%\data,
// qui survit aux déploiements et aux redémarrages.
public static class AppPaths
{
    public static string UploadsDir { get; set; } = "";
}
