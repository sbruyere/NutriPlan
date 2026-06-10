using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

public class DiaryService(IDbContextFactory<NutriPlanContext> factory, IWebHostEnvironment env)
{
    public static readonly (string Label, string Icon, TimeOnly DefaultTime)[] MealSlots =
    [
        ("Petit-déjeuner",       "☕", new TimeOnly(8,  0)),
        ("Collation matin",      "🍎", new TimeOnly(10, 0)),
        ("Déjeuner",             "🍽️", new TimeOnly(12, 30)),
        ("Collation après-midi", "🫐", new TimeOnly(16, 0)),
        ("Dîner",                "🌙", new TimeOnly(19, 30)),
        ("Collation soir",       "🌛", new TimeOnly(21, 0)),
    ];

    // ── Carnets ──────────────────────────────────────────────────────────────

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
        return await ctx.FoodDiaries.FirstOrDefaultAsync(d => d.AccessCode == accessCode);
    }

    public async Task<bool> ValidatePinAsync(string accessCode, string pin)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FirstOrDefaultAsync(d => d.AccessCode == accessCode);
        if (diary == null) return false;
        if (diary.PinHash == null) return true;
        return BCrypt.Net.BCrypt.Verify(pin, diary.PinHash);
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
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary != null) { ctx.FoodDiaries.Remove(diary); await ctx.SaveChangesAsync(); }
    }

    // ── Journées ─────────────────────────────────────────────────────────────

    public async Task<DailyLog> GetOrCreateDailyLogAsync(int diaryId, DateOnly date)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var log = await ctx.DailyLogs
            .Include(l => l.Events).ThenInclude(e => e.FoodEntries)
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == date);
        if (log != null) return log;
        log = new DailyLog { FoodDiaryId = diaryId, Date = date };
        ctx.DailyLogs.Add(log);
        await ctx.SaveChangesAsync();
        return log;
    }

    public async Task<DailyLog?> GetDailyLogFullAsync(int diaryId, DateOnly date)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var log = await ctx.DailyLogs
            .Include(l => l.Events.OrderBy(e => e.CreatedAt))
                .ThenInclude(e => e.FoodEntries.OrderBy(f => f.CreatedAt))
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == date);
        if (log != null)
        {
            var eventIds = log.Events.Select(e => e.Id).ToList();
            var drinks = await ctx.DrinkEntries
                .Where(d => eventIds.Contains(d.DayEventId))
                .OrderBy(d => d.CreatedAt).ToListAsync();
            var photos = await ctx.MealPhotos
                .Where(p => eventIds.Contains(p.DayEventId))
                .OrderBy(p => p.CreatedAt).ToListAsync();
            foreach (var evt in log.Events)
            {
                evt.DrinkEntries = drinks.Where(d => d.DayEventId == evt.Id).ToList();
                evt.Photos = photos.Where(p => p.DayEventId == evt.Id).ToList();
            }
        }
        return log;
    }

    public async Task<List<DailyLog>> GetAllDailyLogsAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.DailyLogs
            .Include(l => l.Events).ThenInclude(e => e.FoodEntries)
            .Where(l => l.FoodDiaryId == diaryId)
            .OrderByDescending(l => l.Date)
            .ToListAsync();
    }

    public async Task UpdateDailyLogSummaryAsync(int logId, string? hydration, string? emotions, string? notes)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var log = await ctx.DailyLogs.FindAsync(logId);
        if (log == null) return;
        log.Hydration = string.IsNullOrWhiteSpace(hydration) ? null : hydration;
        log.Emotions = string.IsNullOrWhiteSpace(emotions) ? null : emotions;
        log.GeneralNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        await ctx.SaveChangesAsync();
    }

    // ── Événements (jalons repas + activités) ────────────────────────────────

    public async Task<DayEvent> GetOrCreateMealSlotEventAsync(int dailyLogId, int slotIndex)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var evt = await ctx.DayEvents
            .Include(e => e.FoodEntries)
            .FirstOrDefaultAsync(e => e.DailyLogId == dailyLogId && e.MealSlot == slotIndex);
        if (evt != null) return evt;
        var (label, _, defaultTime) = MealSlots[slotIndex];
        evt = new DayEvent
        {
            DailyLogId = dailyLogId,
            EventType = DayEventType.Meal,
            MealSlot = slotIndex,
            Label = label,
            Time = defaultTime,
            CreatedAt = DateTime.UtcNow
        };
        ctx.DayEvents.Add(evt);
        await ctx.SaveChangesAsync();
        return evt;
    }

    public async Task<DayEvent> AddConcurrentActivityAsync(int dailyLogId, int mealSlotIndex)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var mealTime = MealSlots[mealSlotIndex].DefaultTime;
        var evt = new DayEvent
        {
            DailyLogId = dailyLogId,
            EventType = DayEventType.Activity,
            ConcurrentWithMealSlot = mealSlotIndex,
            Label = "Activité",
            Time = mealTime,
            CreatedAt = DateTime.UtcNow
        };
        ctx.DayEvents.Add(evt);
        await ctx.SaveChangesAsync();
        return evt;
    }

    public async Task<DayEvent> AddActivityAfterSlotAsync(int dailyLogId, int? afterSlot)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        TimeOnly? defaultTime = null;
        if (afterSlot.HasValue && afterSlot.Value < MealSlots.Length - 1)
        {
            var a = MealSlots[afterSlot.Value].DefaultTime;
            var b = MealSlots[afterSlot.Value + 1].DefaultTime;
            var mid = new TimeOnly((a.Hour + b.Hour) / 2, 0);
            defaultTime = mid;
        }
        else if (afterSlot.HasValue)
        {
            defaultTime = MealSlots[afterSlot.Value].DefaultTime.AddHours(1);
        }
        var evt = new DayEvent
        {
            DailyLogId = dailyLogId,
            EventType = DayEventType.Activity,
            ActivityAfterSlot = afterSlot,
            Label = "Activité",
            Time = defaultTime,
            CreatedAt = DateTime.UtcNow
        };
        ctx.DayEvents.Add(evt);
        await ctx.SaveChangesAsync();
        return evt;
    }

    public async Task UpdateEventAsync(int eventId, string label, TimeOnly? time,
        int? hungerBefore, int? satietyAfter, string? sensations, string? emotions,
        int? durationMinutes, string? intensity, string? activityNotes)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var evt = await ctx.DayEvents.FindAsync(eventId);
        if (evt == null) return;
        evt.Label = label;
        evt.Time = time;
        evt.HungerBefore = hungerBefore;
        evt.SatietyAfter = satietyAfter;
        evt.Sensations = sensations;
        evt.Emotions = emotions;
        evt.DurationMinutes = durationMinutes;
        evt.Intensity = intensity;
        evt.ActivityNotes = activityNotes;
        await ctx.SaveChangesAsync();
    }

    public async Task<MealPhoto> AddPhotoAsync(int dayEventId, Stream imageStream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        var safeName = $"{Guid.NewGuid()}{ext}";
        var uploadsDir = Path.Combine(env.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);
        var fullPath = Path.Combine(uploadsDir, safeName);
        await using (var fs = File.Create(fullPath))
            await imageStream.CopyToAsync(fs);
        await using var ctx = await factory.CreateDbContextAsync();
        var photo = new MealPhoto { DayEventId = dayEventId, FilePath = $"/uploads/{safeName}", CreatedAt = DateTime.UtcNow };
        ctx.MealPhotos.Add(photo);
        await ctx.SaveChangesAsync();
        return photo;
    }

    public async Task DeletePhotoAsync(int photoId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var photo = await ctx.MealPhotos.FindAsync(photoId);
        if (photo == null) return;
        var fullPath = Path.Combine(env.WebRootPath, photo.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) File.Delete(fullPath);
        ctx.MealPhotos.Remove(photo);
        await ctx.SaveChangesAsync();
    }

    public async Task DeleteEventAsync(int eventId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var evt = await ctx.DayEvents.FindAsync(eventId);
        if (evt != null) { ctx.DayEvents.Remove(evt); await ctx.SaveChangesAsync(); }
    }

    public async Task MoveActivityAsync(int eventId, int? newAfterSlot)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var evt = await ctx.DayEvents.FindAsync(eventId);
        if (evt == null || evt.EventType != DayEventType.Activity) return;
        evt.ActivityAfterSlot = newAfterSlot;
        evt.ConcurrentWithMealSlot = null;
        await ctx.SaveChangesAsync();
    }

    public async Task SwapOrMoveMealSlotAsync(int eventId, int targetSlot)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var source = await ctx.DayEvents.FindAsync(eventId);
        if (source == null || !source.MealSlot.HasValue) return;
        var target = await ctx.DayEvents.FirstOrDefaultAsync(e =>
            e.DailyLogId == source.DailyLogId &&
            e.MealSlot == targetSlot &&
            e.EventType == DayEventType.Meal);
        int sourceSlot = source.MealSlot.Value;
        source.MealSlot = targetSlot;
        if (target != null) target.MealSlot = sourceSlot;
        await ctx.SaveChangesAsync();
    }

    public async Task<FoodEntry> AddFoodEntryAsync(int dayEventId, string foodName, string? quantity, string? preparationMethod, string? groupName = null)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = new FoodEntry
        {
            DayEventId = dayEventId,
            FoodName = foodName,
            Quantity = string.IsNullOrWhiteSpace(quantity) ? null : quantity,
            PreparationMethod = string.IsNullOrWhiteSpace(preparationMethod) ? null : preparationMethod,
            GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName,
            CreatedAt = DateTime.UtcNow
        };
        ctx.FoodEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return entry;
    }

    public async Task<DrinkEntry> AddDrinkEntryAsync(int dayEventId, string drinkName)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = new DrinkEntry { DayEventId = dayEventId, DrinkName = drinkName, CreatedAt = DateTime.UtcNow };
        ctx.DrinkEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return entry;
    }

    public async Task DeleteDrinkEntryAsync(int drinkEntryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = await ctx.DrinkEntries.FindAsync(drinkEntryId);
        if (entry != null) { ctx.DrinkEntries.Remove(entry); await ctx.SaveChangesAsync(); }
    }

    public async Task DeleteFoodEntryAsync(int entryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = await ctx.FoodEntries.FindAsync(entryId);
        if (entry != null) { ctx.FoodEntries.Remove(entry); await ctx.SaveChangesAsync(); }
    }

    private static string GenerateAccessCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 8).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
