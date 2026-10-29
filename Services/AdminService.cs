using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

/// <summary>
/// Administration de la plateforme : compte initial, authentification (avec
/// verrou anti-force-brute), vue d'ensemble, gestion des nutritionnistes et
/// des carnets, outils système. L'administration ne donne JAMAIS accès au
/// contenu santé des carnets (repas, photos, ressentis) — elle gère, elle ne
/// consulte pas.
/// </summary>
public class AdminService(IDbContextFactory<NutriPlanContext> factory, DiaryService diaryService, StravaService stravaService)
{
    // ── Compte et authentification ───────────────────────────────────────────

    public async Task<bool> HasAdminAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Admins.AnyAsync();
    }

    /// <summary>Crée le tout premier administrateur — refuse si un compte existe déjà.</summary>
    public async Task<(bool Success, string? Error)> CreateInitialAdminAsync(string name, string email, string password)
    {
        name = name.Trim();
        email = email.Trim().ToLowerInvariant();
        if (name.Length == 0 || email.Length < 5 || !email.Contains('@'))
            return (false, "Nom ou email invalide.");
        if (password.Length < 10)
            return (false, "Le mot de passe administrateur doit comporter au moins 10 caractères.");
        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.Admins.AnyAsync())
            return (false, "Un administrateur existe déjà — la configuration initiale est fermée.");
        ctx.Admins.Add(new Admin
        {
            Name = name,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        });
        await ctx.SaveChangesAsync();
        return (true, null);
    }

    // Anti-force-brute : 5 échecs par email → verrou 15 minutes.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Fails, DateTime LockedUntil)> loginAttempts = new();

    public async Task<Admin?> AuthenticateAsync(string email, string password)
    {
        email = email.Trim().ToLowerInvariant();
        if (loginAttempts.TryGetValue(email, out var state) && state.LockedUntil > DateTime.UtcNow)
            return null;
        await using var ctx = await factory.CreateDbContextAsync();
        var admin = await ctx.Admins.FirstOrDefaultAsync(a => a.Email == email);
        var ok = admin != null && BCrypt.Net.BCrypt.Verify(password, admin.PasswordHash);
        if (!ok)
        {
            var fails = (loginAttempts.TryGetValue(email, out var s) ? s.Fails : 0) + 1;
            loginAttempts[email] = (fails, fails >= 5 ? DateTime.UtcNow.AddMinutes(15) : DateTime.MinValue);
            await Task.Delay(300);
            return null;
        }
        loginAttempts.TryRemove(email, out _);
        admin!.LastLoginAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return admin;
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(int adminId, string current, string newPassword)
    {
        if (newPassword.Length < 10) return (false, "Au moins 10 caractères.");
        await using var ctx = await factory.CreateDbContextAsync();
        var admin = await ctx.Admins.FindAsync(adminId);
        if (admin == null || !BCrypt.Net.BCrypt.Verify(current, admin.PasswordHash))
            return (false, "Mot de passe actuel incorrect.");
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await ctx.SaveChangesAsync();
        return (true, null);
    }

    // ── Vue d'ensemble ───────────────────────────────────────────────────────

    public record Overview(
        int Nutritionists, int SuspendedNutritionists,
        int Diaries, int ManagedDiaries, int DiariesWithPin, int DiariesWithStrava, int ActiveDiaries7d,
        int DailyLogs, int Meals, int Activities, int Photos, int WeightEntries, int DayComments,
        int CatalogCiqual, int CatalogUsda,
        long DbSizeBytes, long UploadsSizeBytes, int UploadsCount,
        int BackupCount, DateTime? LastBackupUtc, bool IsAzure, bool StravaConfigured);

    public async Task<Overview> GetOverviewAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var since = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var dbFile = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(AppPaths.ConnectionString).DataSource;
        long dbSize = File.Exists(dbFile) ? new FileInfo(dbFile).Length : 0;
        long uploadsSize = 0; var uploadsCount = 0;
        try
        {
            var files = Directory.Exists(AppPaths.UploadsDir) ? Directory.GetFiles(AppPaths.UploadsDir) : [];
            uploadsCount = files.Length;
            uploadsSize = files.Sum(f => new FileInfo(f).Length);
        }
        catch { /* dossier inaccessible : tailles à zéro */ }
        var backupsDir = Path.Combine(AppPaths.DataDir, "backups");
        var backups = Directory.Exists(backupsDir) ? Directory.GetFiles(backupsDir, "nutriplan*.db") : [];

        return new Overview(
            await ctx.Nutritionists.CountAsync(),
            await ctx.Nutritionists.CountAsync(n => n.IsSuspended),
            await ctx.FoodDiaries.CountAsync(),
            await ctx.FoodDiaries.CountAsync(d => d.NutritionistId != null),
            await ctx.FoodDiaries.CountAsync(d => d.PinHash != null),
            await ctx.FoodDiaries.CountAsync(d => d.StravaAthleteId != null),
            await ctx.DailyLogs.Where(l => l.Date >= since).Select(l => l.FoodDiaryId).Distinct().CountAsync(),
            await ctx.DailyLogs.CountAsync(),
            await ctx.DayEvents.CountAsync(e => e.EventType == DayEventType.Meal),
            await ctx.DayEvents.CountAsync(e => e.EventType == DayEventType.Activity),
            await ctx.MealPhotos.CountAsync(),
            await ctx.WeightEntries.CountAsync(),
            await ctx.DayComments.CountAsync(),
            await ctx.CatalogFoods.CountAsync(c => c.Source != "usda"),
            await ctx.CatalogFoods.CountAsync(c => c.Source == "usda"),
            dbSize, uploadsSize, uploadsCount,
            backups.Length,
            backups.Length > 0 ? backups.Max(f => File.GetLastWriteTimeUtc(f)) : null,
            AppPaths.IsAzure, stravaService.IsConfigured);
    }

    // ── Nutritionnistes ──────────────────────────────────────────────────────

    public record NutritionistRow(int Id, string Name, string Email, DateTime CreatedAt, bool IsSuspended,
        int DiaryCount, DateOnly? LastActivity);

    public async Task<List<NutritionistRow>> GetNutritionistsAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Nutritionists
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NutritionistRow(
                n.Id, n.Name, n.Email, n.CreatedAt, n.IsSuspended,
                n.Diaries.Count,
                n.Diaries.SelectMany(d => d.DailyLogs).Max(l => (DateOnly?)l.Date)))
            .ToListAsync();
    }

    public async Task SetNutritionistSuspendedAsync(int id, bool suspended)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.Nutritionists.FindAsync(id) is { } n)
        {
            n.IsSuspended = suspended;
            await ctx.SaveChangesAsync();
        }
    }

    /// <summary>Nouveau mot de passe temporaire, affiché une seule fois à l'administrateur.</summary>
    public async Task<string?> ResetNutritionistPasswordAsync(int id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.Nutritionists.FindAsync(id) is not { } n) return null;
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var temp = new string(Enumerable.Range(0, 14).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
        n.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temp);
        await ctx.SaveChangesAsync();
        return temp;
    }

    /// <summary>Supprime le compte ; ses carnets sont DÉTACHÉS (conservés, autonomes).</summary>
    public async Task DeleteNutritionistAsync(int id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.FoodDiaries.Where(d => d.NutritionistId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.NutritionistId, (int?)null)
                .SetProperty(d => d.IsNutritionistManaged, false));
        await ctx.Nutritionists.Where(n => n.Id == id).ExecuteDeleteAsync();
    }

    // ── Carnets ──────────────────────────────────────────────────────────────

    public record DiaryRow(int Id, string AccessCode, string? PatientAlias, string? NutritionistName,
        DateTime CreatedAt, int DayCount, DateOnly? LastActivity, bool HasPin, bool HasStrava);

    public async Task<List<DiaryRow>> GetDiariesAsync(string? search = null, int limit = 200)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var query = ctx.FoodDiaries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(d => d.AccessCode.Contains(s.ToUpper()) || (d.PatientAlias != null && d.PatientAlias.Contains(s)));
        }
        // Tri AVANT la projection : l'ordre sur la propriété du record projeté
        // (sous-requête Max) n'est pas traduisible par EF.
        return await query
            .OrderByDescending(d => d.DailyLogs.Max(l => (DateOnly?)l.Date))
            .ThenByDescending(d => d.CreatedAt)
            .Select(d => new DiaryRow(
                d.Id, d.AccessCode, d.PatientAlias,
                d.Nutritionist != null ? d.Nutritionist.Name : null,
                d.CreatedAt, d.DailyLogs.Count,
                d.DailyLogs.Max(l => (DateOnly?)l.Date),
                d.PinHash != null, d.StravaAthleteId != null))
            .Take(limit)
            .ToListAsync();
    }

    public Task DeleteDiaryAsync(int diaryId) => diaryService.DeleteDiaryAsync(diaryId);

    public async Task RemovePinAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.FoodDiaries.Where(d => d.Id == diaryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PinHash, (string?)null));
    }

    public Task DisconnectStravaAsync(int diaryId) => stravaService.DisconnectAsync(diaryId);

    public async Task DetachDiaryAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.FoodDiaries.Where(d => d.Id == diaryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.NutritionistId, (int?)null)
                .SetProperty(d => d.IsNutritionistManaged, false));
    }

    // ── Système ──────────────────────────────────────────────────────────────

    /// <summary>Sauvegarde immédiate de la base (API de backup SQLite, sûre avec WAL).</summary>
    public async Task<(bool Success, string Message)> BackupNowAsync()
    {
        try
        {
            var backupsDir = Path.Combine(AppPaths.DataDir, "backups");
            Directory.CreateDirectory(backupsDir);
            var backupFile = Path.Combine(backupsDir, $"nutriplan-manuel-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.db");
            await Task.Run(() =>
            {
                using var source = new Microsoft.Data.Sqlite.SqliteConnection(AppPaths.ConnectionString);
                using var target = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupFile}");
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            });
            var size = new FileInfo(backupFile).Length;
            return (true, $"Sauvegarde créée : {Path.GetFileName(backupFile)} ({size / 1024.0 / 1024.0:0.#} Mo)");
        }
        catch (Exception ex)
        {
            return (false, $"Sauvegarde impossible : {ex.Message}");
        }
    }
}
