using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

public class DiaryService(IDbContextFactory<NutriPlanContext> factory)
{
    public async Task<FoodDiary> CreateAnonymousDiaryAsync(string? pin = null)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = new FoodDiary
        {
            AccessCode = GenerateAccessCode(),
            PinHash = pin != null ? BCrypt.Net.BCrypt.HashPassword(pin) : null,
            IsNutritionistManaged = false
        };
        ctx.FoodDiaries.Add(diary);
        await ctx.SaveChangesAsync();
        return diary;
    }

    public async Task<FoodDiary> CreateNutritionistDiaryAsync(int nutritionistId, string patientAlias, string? pin = null, string? notes = null)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = new FoodDiary
        {
            AccessCode = GenerateAccessCode(),
            PinHash = pin != null ? BCrypt.Net.BCrypt.HashPassword(pin) : null,
            PatientAlias = patientAlias,
            Notes = notes,
            NutritionistId = nutritionistId,
            IsNutritionistManaged = true
        };
        ctx.FoodDiaries.Add(diary);
        await ctx.SaveChangesAsync();
        return diary;
    }

    public async Task<FoodDiary?> GetDiaryByCodeAsync(string accessCode)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.FoodDiaries
            .FirstOrDefaultAsync(d => d.AccessCode == accessCode);
    }

    public async Task<bool> ValidatePinAsync(string accessCode, string pin)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FirstOrDefaultAsync(d => d.AccessCode == accessCode);
        if (diary == null) return false;
        if (diary.PinHash == null) return true;
        return BCrypt.Net.BCrypt.Verify(pin, diary.PinHash);
    }

    public async Task<DailyLog> GetOrCreateDailyLogAsync(int diaryId, DateOnly date)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var log = await ctx.DailyLogs
            .Include(l => l.FoodEntries)
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == date);

        if (log != null) return log;

        log = new DailyLog { FoodDiaryId = diaryId, Date = date };
        ctx.DailyLogs.Add(log);
        await ctx.SaveChangesAsync();
        return log;
    }

    public async Task<DailyLog?> GetDailyLogWithEntriesAsync(int diaryId, DateOnly date)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.DailyLogs
            .Include(l => l.FoodEntries.OrderBy(e => e.Time))
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == date);
    }

    public async Task<List<DailyLog>> GetAllDailyLogsAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.DailyLogs
            .Include(l => l.FoodEntries)
            .Where(l => l.FoodDiaryId == diaryId)
            .OrderByDescending(l => l.Date)
            .ToListAsync();
    }

    public async Task AddFoodEntryAsync(int dailyLogId, FoodEntry entry)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        entry.DailyLogId = dailyLogId;
        entry.CreatedAt = DateTime.UtcNow;
        ctx.FoodEntries.Add(entry);
        await ctx.SaveChangesAsync();
    }

    public async Task DeleteFoodEntryAsync(int entryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = await ctx.FoodEntries.FindAsync(entryId);
        if (entry != null)
        {
            ctx.FoodEntries.Remove(entry);
            await ctx.SaveChangesAsync();
        }
    }

    public async Task UpdateDailyLogNotesAsync(int logId, string? notes)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var log = await ctx.DailyLogs.FindAsync(logId);
        if (log != null)
        {
            log.Notes = notes;
            await ctx.SaveChangesAsync();
        }
    }

    public async Task<List<FoodDiary>> GetNutritionistDiariesAsync(int nutritionistId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.FoodDiaries
            .Include(d => d.DailyLogs)
            .Where(d => d.NutritionistId == nutritionistId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task DeleteDiaryAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries
            .Include(d => d.DailyLogs).ThenInclude(l => l.FoodEntries)
            .FirstOrDefaultAsync(d => d.Id == diaryId);
        if (diary != null)
        {
            ctx.FoodDiaries.Remove(diary);
            await ctx.SaveChangesAsync();
        }
    }

    private static string GenerateAccessCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 8).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
