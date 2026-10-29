using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

// Trousseau de clés IA de la saisie express : plusieurs profils enregistrés
// (fournisseur + modèle + clé), un seul actif à la fois — pointeur
// « ai.activeProfile » dans AppSettings ; vide = analyse locale uniquement.
// Révoquer un profil supprime sa clé de la base.
public class AiProfileService(IDbContextFactory<NutriPlanContext> factory, SettingsService settings)
{
    public const string ActiveKey = "ai.activeProfile";

    public static bool IsComplete(AiProfile p) => p.Provider switch
    {
        "anthropic" or "openai" or "openrouter" => p.ApiKey != "" && p.Model != "",
        "azure" => p.ApiKey != "" && p.Model != "" && p.Endpoint != "",
        _ => false,
    };

    public async Task<List<AiProfile>> ListAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.AiProfiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
    }

    public async Task<int?> GetActiveIdAsync() =>
        int.TryParse(await settings.GetAsync(ActiveKey), out var id) ? id : null;

    public async Task<AiProfile?> GetActiveAsync()
    {
        if (await GetActiveIdAsync() is not int id) return null;
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.AiProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<AiProfile> AddAsync(string provider, string model, string endpoint, string apiKey, bool activate = true)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var profile = new AiProfile
        {
            Provider = provider,
            Model = model.Trim(),
            Endpoint = endpoint.Trim(),
            ApiKey = apiKey.Trim(),
        };
        ctx.AiProfiles.Add(profile);
        await ctx.SaveChangesAsync();
        if (activate) await settings.SetAsync(ActiveKey, profile.Id.ToString());
        return profile;
    }

    public async Task SetActiveAsync(int? id) =>
        await settings.SetAsync(ActiveKey, id?.ToString() ?? "");

    public async Task RevokeAsync(int id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.AiProfiles.FindAsync(id) is { } profile)
        {
            ctx.AiProfiles.Remove(profile);
            await ctx.SaveChangesAsync();
        }
        if (await GetActiveIdAsync() == id)
            await settings.SetAsync(ActiveKey, ""); // retour à l'analyse locale
    }
}
