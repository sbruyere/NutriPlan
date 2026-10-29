using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

// Réglages de plateforme (clé → valeur) avec cache mémoire : lus sur le chemin
// chaud de la saisie express, écrits uniquement depuis le backend admin.
public class SettingsService(IDbContextFactory<NutriPlanContext> factory)
{
    static readonly ConcurrentDictionary<string, string> cache = new();
    static volatile bool loaded;

    async Task EnsureLoadedAsync()
    {
        if (loaded) return;
        await using var ctx = await factory.CreateDbContextAsync();
        foreach (var s in await ctx.AppSettings.AsNoTracking().ToListAsync())
            cache[s.Key] = s.Value;
        loaded = true;
    }

    public async Task<string> GetAsync(string key)
    {
        await EnsureLoadedAsync();
        return cache.GetValueOrDefault(key, "");
    }

    public async Task SetAsync(string key, string value)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var row = await ctx.AppSettings.FindAsync(key);
        if (row == null) ctx.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await ctx.SaveChangesAsync();
        cache[key] = value;
    }

}
