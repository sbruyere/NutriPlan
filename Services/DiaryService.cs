using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace NutriPlan.Services;

public class DiaryService(IDbContextFactory<NutriPlanContext> factory)
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

    // ── Hydratation — volumes estimés (ml), partagés entre les vues ─────────

    public static readonly IReadOnlyDictionary<string, int> DrinkVolumesMl = new Dictionary<string, int>
    {
        { "Eau (1 verre)",      250 }, { "Eau (75cl)",        750 }, { "Eau (1,5L)",       1500 },
        { "Eau gazeuse",        250 }, { "Café expresso",      30 }, { "Café allongé",      120 },
        { "Thé (grande tasse)", 350 }, { "Infusion",          300 }, { "Matcha",            200 },
        { "Chocolat chaud",     250 }, { "Lait",              200 }, { "Kéfir",             200 },
        { "Kombucha",           250 }, { "Smoothie",          250 }, { "Jus de fruit",      200 },
        { "Sirop",              200 }, { "Coca-Cola",         330 }, { "Boisson isotonique", 500 },
        { "Électrolytes",       500 }, { "Bière",             250 }, { "Vin rouge",         120 },
        { "Vin rosé",           120 }, { "Vin blanc",         120 },
    };

    public static int EstimateHydrationMl(IEnumerable<DrinkEntry> drinks) =>
        drinks.Sum(d => DrinkVolumesMl.TryGetValue(d.DrinkName, out var v) ? v : 200);

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

    public async Task AssignNutritionistAsync(int diaryId, int nutritionistId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null) return;
        diary.NutritionistId = nutritionistId;
        diary.IsNutritionistManaged = true;
        await ctx.SaveChangesAsync();
    }

    public async Task RemoveNutritionistAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null) return;
        diary.NutritionistId = null;
        diary.IsNutritionistManaged = false;
        await ctx.SaveChangesAsync();
    }

    public async Task<Nutritionist?> GetAssignedNutritionistAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.FoodDiaries
            .Where(d => d.Id == diaryId)
            .Select(d => d.Nutritionist)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Définit, change ou supprime le PIN d'un carnet. Si un PIN existe déjà,
    /// <paramref name="currentPin"/> doit être valide. <paramref name="newPin"/> null ou vide = suppression.
    /// </summary>
    public async Task<bool> UpdatePinAsync(int diaryId, string? currentPin, string? newPin)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null) return false;
        if (diary.PinHash != null &&
            (string.IsNullOrEmpty(currentPin) || !BCrypt.Net.BCrypt.Verify(currentPin, diary.PinHash)))
            return false;
        diary.PinHash = string.IsNullOrEmpty(newPin) ? null : BCrypt.Net.BCrypt.HashPassword(newPin);
        await ctx.SaveChangesAsync();
        return true;
    }

    /// <summary>Noms d'aliments déjà saisis dans ce carnet, du plus fréquent au moins fréquent.</summary>
    public async Task<List<string>> GetFoodSuggestionsAsync(int diaryId, int limit = 100)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.FoodEntries
            .Where(f => f.DayEvent.DailyLog.FoodDiaryId == diaryId)
            .GroupBy(f => f.FoodName)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(f => f.CreatedAt))
            .Select(g => g.Key)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>Aliment favori d'un carnet : nom + dernière quantité/préparation utilisée + nombre d'utilisations.</summary>
    public sealed record FrequentFood(string FoodName, string? Quantity, string? QuantityUnit, string? PreparationMethod, int UseCount);

    /// <summary>Composant d'une composition alimentaire favorite.</summary>
    public sealed record CompositionItem(string FoodName, string? Quantity, string? QuantityUnit, string? PreparationMethod);

    /// <summary>Composition favorite d'un carnet : nom + composantes de sa dernière utilisation.</summary>
    public sealed record FrequentComposition(string Name, int UseCount, List<CompositionItem> Items);

    /// <summary>
    /// Les compositions alimentaires les plus utilisées du carnet (une « utilisation » =
    /// un repas où le nom de composition apparaît), avec les composantes et quantités
    /// de la dernière utilisation — pour re-proposer un « Sandwich thon » complet en un tap.
    /// </summary>
    public async Task<List<FrequentComposition>> GetTopCompositionsAsync(int diaryId, int limit = 4)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entries = await ctx.FoodEntries
            .Where(f => f.GroupName != null && f.DayEvent.DailyLog.FoodDiaryId == diaryId)
            .Select(f => new { f.DayEventId, f.GroupName, f.FoodName, f.Quantity, f.QuantityUnit, f.PreparationMethod, f.CreatedAt })
            .ToListAsync();
        return entries
            .GroupBy(e => e.GroupName!)
            .Select(g =>
            {
                var uses = g.GroupBy(e => e.DayEventId).ToList();
                var lastUse = uses.OrderByDescending(u => u.Max(e => e.CreatedAt)).First();
                return new FrequentComposition(
                    g.Key,
                    uses.Count,
                    lastUse.OrderBy(e => e.CreatedAt)
                        .Select(e => new CompositionItem(e.FoodName, e.Quantity, e.QuantityUnit, e.PreparationMethod))
                        .ToList());
            })
            .OrderByDescending(c => c.UseCount)
            .ThenBy(c => c.Name)
            .Take(limit)
            .ToList();
    }

    /// <summary>Ajoute une composition complète (tous ses aliments) à un événement, en un appel.</summary>
    public async Task<List<FoodEntry>> AddCompositionAsync(int dayEventId, string groupName, IEnumerable<CompositionItem> items)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var created = new List<FoodEntry>();
        var now = DateTime.UtcNow;
        var nextSort = ((await ctx.FoodEntries.Where(f => f.DayEventId == dayEventId).MaxAsync(f => (int?)f.SortOrder)) ?? -1) + 1;
        var i = 0;
        foreach (var item in items)
        {
            var entry = new FoodEntry
            {
                DayEventId = dayEventId,
                FoodName = item.FoodName,
                Quantity = string.IsNullOrWhiteSpace(item.Quantity) ? null : item.Quantity,
                QuantityUnit = string.IsNullOrWhiteSpace(item.QuantityUnit) ? null : item.QuantityUnit,
                PreparationMethod = string.IsNullOrWhiteSpace(item.PreparationMethod) ? null : item.PreparationMethod,
                GroupName = groupName,
                SortOrder = nextSort + i,
                // Millisecondes croissantes : préserve l'ordre des composantes à l'affichage
                CreatedAt = now.AddMilliseconds(i++)
            };
            ctx.FoodEntries.Add(entry);
            created.Add(entry);
        }
        await ctx.SaveChangesAsync();
        return created;
    }

    /// <summary>
    /// Les aliments les plus saisis du carnet, avec la quantité/préparation de leur
    /// dernière utilisation — pour les raccourcis d'ajout en un tap.
    /// </summary>
    public async Task<List<FrequentFood>> GetTopFoodsAsync(int diaryId, int limit = 6)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var stats = await ctx.FoodEntries
            .Where(f => f.DayEvent.DailyLog.FoodDiaryId == diaryId)
            .GroupBy(f => f.FoodName)
            .Select(g => new { Name = g.Key, Count = g.Count(), LastUsed = g.Max(f => f.CreatedAt) })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.LastUsed)
            .Take(limit)
            .ToListAsync();
        if (stats.Count == 0) return [];
        var names = stats.Select(s => s.Name).ToList();
        var latestEntries = await ctx.FoodEntries
            .Where(f => f.DayEvent.DailyLog.FoodDiaryId == diaryId && names.Contains(f.FoodName))
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();
        return stats.Select(s =>
        {
            var last = latestEntries.First(f => f.FoodName == s.Name);
            return new FrequentFood(s.Name, last.Quantity, last.QuantityUnit, last.PreparationMethod, s.Count);
        }).ToList();
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
            .Include(l => l.Comments.OrderBy(c => c.CreatedAt))
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

    /// <summary>
    /// Journées complètes (aliments + boissons) sur une fenêtre glissante — pour les
    /// synthèses hebdomadaires. Ordonnées de la plus ancienne à la plus récente.
    /// </summary>
    public async Task<List<DailyLog>> GetRecentLogsFullAsync(int diaryId, DateOnly endDate, int days = 7)
    {
        var startDate = endDate.AddDays(-(days - 1));
        await using var ctx = await factory.CreateDbContextAsync();
        var logs = await ctx.DailyLogs
            .Include(l => l.Events).ThenInclude(e => e.FoodEntries)
            .Where(l => l.FoodDiaryId == diaryId && l.Date >= startDate && l.Date <= endDate)
            .OrderBy(l => l.Date)
            .ToListAsync();
        var eventIds = logs.SelectMany(l => l.Events).Select(e => e.Id).ToList();
        var drinks = await ctx.DrinkEntries.Where(d => eventIds.Contains(d.DayEventId)).ToListAsync();
        foreach (var evt in logs.SelectMany(l => l.Events))
            evt.DrinkEntries = drinks.Where(d => d.DayEventId == evt.Id).ToList();
        return logs;
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

    // Déplace toute une journée (repas, activités, photos, notes) vers une date VIDE
    // — pour corriger une saisie faite sur le mauvais jour. Refuse si la date cible
    // contient déjà quelque chose : pas de fusion ni d'écrasement silencieux.
    public async Task<bool> MoveDailyLogAsync(int diaryId, DateOnly from, DateOnly to)
    {
        if (from == to) return false;
        await using var ctx = await factory.CreateDbContextAsync();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var source = await ctx.DailyLogs
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == from);
        if (source == null) return false;
        var target = await ctx.DailyLogs.Include(l => l.Events)
            .FirstOrDefaultAsync(l => l.FoodDiaryId == diaryId && l.Date == to);
        if (target != null)
        {
            var targetEmpty = target.Events.Count == 0
                && string.IsNullOrWhiteSpace(target.Hydration)
                && string.IsNullOrWhiteSpace(target.Emotions)
                && string.IsNullOrWhiteSpace(target.GeneralNotes);
            if (!targetEmpty) return false;
            // Log vide résiduel sur la cible : on le supprime pour libérer la date
            // (index unique FoodDiaryId+Date) avant de déplacer la source.
            ctx.DailyLogs.Remove(target);
            await ctx.SaveChangesAsync();
        }
        source.Date = to;
        await ctx.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
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
        int? durationMinutes, string? intensity, string? activityNotes, int? caloriesBurned = null,
        double? distanceKm = null, int? elevationM = null)
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
        evt.CaloriesBurned = caloriesBurned;
        evt.DistanceKm = distanceKm;
        evt.ElevationM = elevationM;
        await ctx.SaveChangesAsync();
    }

    // Les photos de téléphone font couramment 4-12 Mo : on accepte tout et on
    // normalise ICI — orientation EXIF appliquée, côté max 1 600 px, ré-encodage
    // JPEG qualité 82 (≈ 150-400 Ko par photo, quel que soit l'original).
    public async Task<MealPhoto> AddPhotoAsync(int dayEventId, Stream imageStream, string fileName)
    {
        var safeName = $"{Guid.NewGuid()}.jpg";
        var uploadsDir = AppPaths.UploadsDir;
        Directory.CreateDirectory(uploadsDir);
        var fullPath = Path.Combine(uploadsDir, safeName);

        using (var image = await SixLabors.ImageSharp.Image.LoadAsync(imageStream))
        {
            image.Mutate(op => op.AutoOrient());
            if (image.Width > 1600 || image.Height > 1600)
                image.Mutate(op => op.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                    Size = new SixLabors.ImageSharp.Size(1600, 1600),
                }));
            await image.SaveAsJpegAsync(fullPath, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 });
        }
        await using var ctx = await factory.CreateDbContextAsync();
        var photo = new MealPhoto
        {
            DayEventId = dayEventId,
            FilePath = $"/uploads/{safeName}",
            CreatedAt = DateTime.UtcNow,
            SortOrder = ((await ctx.MealPhotos.Where(p => p.DayEventId == dayEventId).MaxAsync(p => (int?)p.SortOrder)) ?? -1) + 1
        };
        ctx.MealPhotos.Add(photo);
        await ctx.SaveChangesAsync();
        return photo;
    }

    public async Task DeletePhotoAsync(int photoId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var photo = await ctx.MealPhotos.FindAsync(photoId);
        if (photo == null) return;
        var fullPath = Path.Combine(AppPaths.UploadsDir, Path.GetFileName(photo.FilePath));
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

    public async Task UpdateEventTimeAsync(int eventId, TimeOnly? time)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var evt = await ctx.DayEvents.FindAsync(eventId);
        if (evt == null) return;
        evt.Time = time;
        await ctx.SaveChangesAsync();
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

    public async Task<FoodEntry> AddFoodEntryAsync(int dayEventId, string foodName, string? quantity, string? quantityUnit, string? preparationMethod, string? groupName = null, int? catalogFoodId = null)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = new FoodEntry
        {
            DayEventId = dayEventId,
            FoodName = foodName,
            Quantity = string.IsNullOrWhiteSpace(quantity) ? null : quantity.Trim(),
            QuantityUnit = string.IsNullOrWhiteSpace(quantityUnit) ? null : quantityUnit.Trim(),
            PreparationMethod = string.IsNullOrWhiteSpace(preparationMethod) ? null : preparationMethod,
            GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName,
            CatalogFoodId = catalogFoodId,
            CreatedAt = DateTime.UtcNow,
            SortOrder = ((await ctx.FoodEntries.Where(f => f.DayEventId == dayEventId).MaxAsync(f => (int?)f.SortOrder)) ?? -1) + 1
        };
        ctx.FoodEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return entry;
    }

    // ── Profil & métabolisme ─────────────────────────────────────────────────

    public record ActivityLevelDef(string Key, string Label, double Factor);

    // Facteurs d'activité (PAL) classiques appliqués au métabolisme de base.
    public static readonly ActivityLevelDef[] ActivityLevels =
    [
        new("sedentaire", "Sédentaire (peu ou pas d'exercice)", 1.2),
        new("leger", "Légèrement actif (1-3 séances/sem.)", 1.375),
        new("actif", "Actif (3-5 séances/sem.)", 1.55),
        new("tres-actif", "Très actif (6-7 séances/sem.)", 1.725),
        new("extreme", "Extrêmement actif (travail physique, 2 entraînements/j)", 1.9),
    ];

    // Métabolisme de base — équation de Mifflin-St Jeor (1990) :
    // 10 × poids(kg) + 6,25 × taille(cm) − 5 × âge + 5 (homme) / − 161 (femme).
    public static double? BasalMetabolicRate(FoodDiary d, int currentYear)
    {
        if (d.WeightKg is not double w || d.HeightCm is not int h || d.BirthYear is not int birthYear || d.Sex is not ("F" or "M"))
            return null;
        var age = Math.Clamp(currentYear - birthYear, 10, 110);
        return 10 * w + 6.25 * h - 5 * age + (d.Sex == "M" ? 5 : -161);
    }

    // Besoins journaliers estimés = métabolisme de base × facteur d'activité.
    public static double? DailyEnergyNeeds(FoodDiary d, int currentYear)
    {
        if (BasalMetabolicRate(d, currentYear) is not double bmr) return null;
        var factor = ActivityLevels.FirstOrDefault(a => a.Key == d.ActivityLevel)?.Factor ?? 1.2;
        return bmr * factor;
    }

    public async Task UpdateProfileAsync(int diaryId, string? sex, int? heightCm, double? weightKg,
        int? birthYear, string? activityLevel, int? dailyKcalTarget, int? dailyBurnTarget)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null) return;
        var previousWeight = diary.WeightKg;
        diary.Sex = sex is "F" or "M" ? sex : null;
        diary.HeightCm = heightCm;
        diary.WeightKg = weightKg;
        diary.BirthYear = birthYear;
        diary.ActivityLevel = ActivityLevels.Any(a => a.Key == activityLevel) ? activityLevel : null;
        diary.DailyKcalTarget = dailyKcalTarget;
        diary.DailyBurnTarget = dailyBurnTarget;
        // Un poids modifié depuis le profil alimente aussi l'historique pondéral (pesée du jour).
        if (weightKg is double kg && kg > 0 && Math.Abs((previousWeight ?? 0) - kg) > 0.01)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var entry = await ctx.WeightEntries.FirstOrDefaultAsync(w => w.FoodDiaryId == diaryId && w.Date == today);
            if (entry == null) ctx.WeightEntries.Add(new WeightEntry { FoodDiaryId = diaryId, Date = today, WeightKg = kg });
            else entry.WeightKg = kg;
        }
        await ctx.SaveChangesAsync();
    }

    // ── Historique pondéral ──────────────────────────────────────────────────

    /// <summary>
    /// Enregistre (ou remplace) la pesée d'un jour ; le profil reflète toujours la
    /// pesée la plus récente. Retourne le poids « courant » du profil après opération.
    /// </summary>
    public async Task<double?> AddWeightAsync(int diaryId, DateOnly date, double weightKg)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var diary = await ctx.FoodDiaries.FindAsync(diaryId);
        if (diary == null || weightKg is <= 20 or >= 400) return diary?.WeightKg;
        var entry = await ctx.WeightEntries.FirstOrDefaultAsync(w => w.FoodDiaryId == diaryId && w.Date == date);
        if (entry == null) ctx.WeightEntries.Add(new WeightEntry { FoodDiaryId = diaryId, Date = date, WeightKg = weightKg });
        else entry.WeightKg = weightKg;
        var latestExisting = await ctx.WeightEntries
            .Where(w => w.FoodDiaryId == diaryId)
            .MaxAsync(w => (DateOnly?)w.Date);
        if (latestExisting == null || date >= latestExisting)
            diary.WeightKg = weightKg;
        await ctx.SaveChangesAsync();
        return diary.WeightKg;
    }

    public async Task<List<WeightEntry>> GetWeightHistoryAsync(int diaryId, int maxDays = 365)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var since = DateOnly.FromDateTime(DateTime.Today).AddDays(-maxDays);
        return await ctx.WeightEntries
            .Where(w => w.FoodDiaryId == diaryId && w.Date >= since)
            .OrderBy(w => w.Date)
            .ToListAsync();
    }

    // ── Commentaires du praticien sur une journée ────────────────────────────

    public async Task AddDayCommentAsync(int diaryId, DateOnly date, string text, string? authorName)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        var log = await GetOrCreateDailyLogAsync(diaryId, date);
        await using var ctx = await factory.CreateDbContextAsync();
        ctx.DayComments.Add(new DayComment
        {
            DailyLogId = log.Id,
            Text = text.Length > 1000 ? text[..1000] : text,
            AuthorName = authorName,
        });
        await ctx.SaveChangesAsync();
    }

    public async Task DeleteDayCommentAsync(int commentId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.DayComments.Where(c => c.Id == commentId).ExecuteDeleteAsync();
    }

    public async Task MarkDayCommentsReadAsync(int dailyLogId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.DayComments
            .Where(c => c.DailyLogId == dailyLogId && !c.ReadByPatient)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReadByPatient, true));
    }

    /// <summary>Jours portant des commentaires non lus (pour notifier à l'ouverture du carnet).</summary>
    public async Task<List<DateOnly>> GetUnreadCommentDatesAsync(int diaryId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.DayComments
            .Where(c => !c.ReadByPatient && c.DailyLog.FoodDiaryId == diaryId)
            .Select(c => c.DailyLog.Date)
            .Distinct().OrderBy(d => d)
            .ToListAsync();
    }

    // ── Analyse : corrélations sensations ↔ aliments ─────────────────────────
    // Association SUR LE MÊME REPAS entre une gêne renseignée et la présence d'un
    // aliment (ou d'un sous-groupe Ciqual). Corrélation ≠ causalité : l'outil met
    // en évidence des pistes qu'un professionnel doit interpréter.

    public static readonly string[] NegativeSensations =
        ["Ballonnement", "Lourdeur", "Brûlures d'estomac", "Nausée", "Fatigue", "Crampes"];

    public static readonly string[] NegativeEmotions =
        ["Anxieux/se", "Stressé(e)", "Triste", "Coupable", "Frustré(e)"];

    public record SymptomCorrelation(string Symptom, string FoodLabel, string Scope,
        int MealsWithFood, int SymptomMeals, double RateWith, double RateWithout, double Lift);

    public async Task<List<SymptomCorrelation>> GetSymptomCorrelationsAsync(int diaryId, DateOnly endDate, int days = 90)
    {
        await GetCatalogAsync(); // index nécessaires à la résolution des sous-groupes
        var logs = await GetRecentLogsFullAsync(diaryId, endDate, days);
        var meals = logs.SelectMany(l => l.Events)
            .Where(e => e.EventType == DayEventType.Meal && e.FoodEntries.Count > 0)
            .ToList();
        if (meals.Count < 8) return [];

        // Étiquettes par repas : chaque aliment individuellement + son sous-groupe Ciqual.
        var display = new Dictionary<string, (string Label, string Scope)>();
        var mealData = new List<(HashSet<string> Symptoms, HashSet<string> Labels)>();
        foreach (var meal in meals)
        {
            var symptoms = (meal.Sensations ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(NegativeSensations.Contains).ToHashSet();
            var labels = new HashSet<string>();
            foreach (var f in meal.FoodEntries)
            {
                var name = f.FoodName.Trim();
                if (name.Length == 0) continue;
                var foodKey = "aliment|" + FoldFoodName(name);
                labels.Add(foodKey);
                display.TryAdd(foodKey, (name, "aliment"));
                if (ResolveCatalogFood(f)?.SubGroupName is string sub && !string.IsNullOrWhiteSpace(sub))
                {
                    var grpKey = "groupe|" + FoldFoodName(sub);
                    labels.Add(grpKey);
                    display.TryAdd(grpKey, (sub, "groupe"));
                }
            }
            mealData.Add((symptoms, labels));
        }

        var results = new List<SymptomCorrelation>();
        foreach (var symptom in NegativeSensations)
        {
            var totalWithSymptom = mealData.Count(d => d.Symptoms.Contains(symptom));
            if (totalWithSymptom < 3) continue;
            foreach (var (key, (label, scope)) in display)
            {
                var withFood = mealData.Where(d => d.Labels.Contains(key)).ToList();
                var withoutFood = mealData.Count - withFood.Count;
                if (withFood.Count < 4 || withoutFood < 4) continue;
                var symptomWith = withFood.Count(d => d.Symptoms.Contains(symptom));
                if (symptomWith < 3) continue;
                var rateWith = (double)symptomWith / withFood.Count;
                var rateWithout = (double)(totalWithSymptom - symptomWith) / withoutFood;
                var lift = rateWithout <= 0 ? (rateWith >= 0.5 ? 10 : 0) : rateWith / rateWithout;
                if (rateWith >= 0.4 && lift >= 1.5)
                    results.Add(new SymptomCorrelation(symptom, label, scope, withFood.Count, symptomWith, rateWith, rateWithout, lift));
            }
        }
        return results
            .OrderByDescending(r => r.Lift).ThenByDescending(r => r.SymptomMeals)
            .Take(10).ToList();
    }

    // ── Rapport de consultation (synthèse pour le nutritionniste) ────────────

    public record NutritionReport(
        int TotalDays, int DaysWithData, int MealCount,
        double? AvgKcal, double? AvgProteinG, double? AvgCarbsG, double? AvgSugarsG,
        double? AvgFatG, double? AvgSatFatG, double? AvgFiberG, double? AvgSaltG,
        double FoodsLinkedShare,
        double? AvgHydrationMl, int TotalActivityMinutes, int TotalBurnedKcal,
        double? AvgHunger, double? AvgSatiety, int EmotionalMealCount,
        double? AvgEatingWindowHours, int BreakfastSkippedDays, double AvgMealsPerDay,
        List<(string Emotion, int Count)> TopEmotions,
        List<SymptomCorrelation> Correlations);

    public async Task<NutritionReport> GetNutritionReportAsync(int diaryId, DateOnly endDate, int days)
    {
        await GetCatalogAsync();
        var logs = await GetRecentLogsFullAsync(diaryId, endDate, days);
        var filledLogs = logs.Where(l => l.Events.Count > 0).ToList();
        var meals = filledLogs.SelectMany(l => l.Events).Where(e => e.EventType == DayEventType.Meal).ToList();
        var allFoods = meals.SelectMany(m => m.FoodEntries).ToList();
        var daysWithData = filledLogs.Count;

        double totalKcal = 0, totalProt = 0, totalCarb = 0, totalSugar = 0,
               totalFat = 0, totalSatFat = 0, totalFiber = 0, totalSalt = 0;
        var linkedFoods = 0;
        foreach (var f in allFoods)
        {
            if (EstimateKcal(f) != null) linkedFoods++;
            totalKcal += EstimateNutrient(f, c => c.KcalPer100g) ?? 0;
            totalProt += EstimateNutrient(f, c => c.ProteinPer100g) ?? 0;
            totalCarb += EstimateNutrient(f, c => c.CarbsPer100g) ?? 0;
            totalSugar += EstimateNutrient(f, c => c.SugarsPer100g) ?? 0;
            totalFat += EstimateNutrient(f, c => c.FatPer100g) ?? 0;
            totalSatFat += EstimateNutrient(f, c => c.SatFatPer100g) ?? 0;
            totalFiber += EstimateNutrient(f, c => c.FiberPer100g) ?? 0;
            totalSalt += EstimateNutrient(f, c => c.SaltPer100g) ?? 0;
        }
        double? Avg(double total) => daysWithData > 0 && linkedFoods > 0 ? total / daysWithData : null;

        var hungers = meals.Where(m => m.HungerBefore.HasValue).Select(m => (double)m.HungerBefore!.Value).ToList();
        var satieties = meals.Where(m => m.SatietyAfter.HasValue).Select(m => (double)m.SatietyAfter!.Value).ToList();

        var emotionalMeals = meals.Count(m =>
            m.HungerBefore is int h && h <= 3 &&
            (m.Emotions ?? "").Split(',', StringSplitOptions.TrimEntries).Any(NegativeEmotions.Contains));

        // Fenêtre alimentaire : premier → dernier événement horodaté de la journée.
        var windows = filledLogs
            .Select(l => l.Events.Where(e => e.Time.HasValue).Select(e => e.Time!.Value).ToList())
            .Where(times => times.Count >= 2)
            .Select(times => (times.Max() - times.Min()).TotalHours)
            .ToList();

        var breakfastSkipped = filledLogs.Count(l => l.Events.All(e => e.MealSlot != 0));

        var topEmotions = meals
            .SelectMany(m => (m.Emotions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(e => e).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => (g.Key, g.Count())).ToList();

        var correlations = await GetSymptomCorrelationsAsync(diaryId, endDate);

        return new NutritionReport(
            days, daysWithData, meals.Count,
            Avg(totalKcal), Avg(totalProt), Avg(totalCarb), Avg(totalSugar),
            Avg(totalFat), Avg(totalSatFat), Avg(totalFiber), Avg(totalSalt),
            allFoods.Count > 0 ? (double)linkedFoods / allFoods.Count : 0,
            daysWithData > 0 ? filledLogs.Average(l => (double)EstimateHydrationMl(l.Events.SelectMany(e => e.DrinkEntries))) : null,
            filledLogs.SelectMany(l => l.Events).Where(e => e.DurationMinutes.HasValue).Sum(e => e.DurationMinutes!.Value),
            filledLogs.SelectMany(l => l.Events).Where(e => e.CaloriesBurned.HasValue).Sum(e => e.CaloriesBurned!.Value),
            hungers.Count > 0 ? hungers.Average() : null,
            satieties.Count > 0 ? satieties.Average() : null,
            emotionalMeals,
            windows.Count > 0 ? windows.Average() : null,
            breakfastSkipped,
            daysWithData > 0 ? (double)meals.Count / daysWithData : 0,
            topEmotions,
            correlations);
    }

    // ── Référentiel Ciqual ───────────────────────────────────────────────────
    // Cache process-wide : ~3 500 lignes immuables après le seed, partagées entre
    // tous les circuits pour filtrer l'autocomplétion et estimer les kcal.
    static IReadOnlyList<CatalogFood>? catalogCache;
    static Dictionary<int, CatalogFood>? catalogById;
    static Dictionary<string, CatalogFood>? catalogByName;

    public async Task<IReadOnlyList<CatalogFood>> GetCatalogAsync()
    {
        if (catalogCache != null) return catalogCache;
        await using var ctx = await factory.CreateDbContextAsync();
        var list = await ctx.CatalogFoods.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        var byId = new Dictionary<int, CatalogFood>();
        var byName = new Dictionary<string, CatalogFood>();
        foreach (var c in list) { byId[c.Id] = c; byName.TryAdd(c.SearchName, c); }
        catalogById = byId;
        catalogByName = byName;
        catalogCache = list;
        return catalogCache;
    }

    // Minuscules sans diacritiques — même pliage que SearchName du seed.
    public static string FoldFoodName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public CatalogFood? FindCatalogFoodByName(string? name)
    {
        if (catalogByName == null || string.IsNullOrWhiteSpace(name)) return null;
        return catalogByName.GetValueOrDefault(FoldFoodName(name.Trim()));
    }

    // Lien explicite (CatalogFoodId) d'abord, correspondance de nom sinon.
    public CatalogFood? ResolveCatalogFood(FoodEntry f)
    {
        if (catalogById == null) return null;
        if (f.CatalogFoodId is int id && catalogById.TryGetValue(id, out var byId)) return byId;
        return FindCatalogFoodByName(f.FoodName);
    }

    // Valeur estimée d'un constituant : teneur/100 g × grammes (quantité × poids d'unité).
    public double? EstimateNutrient(FoodEntry f, Func<CatalogFood, double?> per100)
    {
        var c = ResolveCatalogFood(f);
        if (c == null || per100(c) is not double v100) return null;
        var raw = (f.Quantity ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var qty)) return null;
        var unit = string.IsNullOrWhiteSpace(f.QuantityUnit) ? c.DefaultUnit : f.QuantityUnit!;
        return ToGrams(qty, unit, c.UnitWeightG) is double grams ? v100 * grams / 100.0 : null;
    }

    public double? EstimateKcal(FoodEntry f) => EstimateNutrient(f, c => c.KcalPer100g);

    public static double? ToGrams(double qty, string unit, double? unitWeightG) => unit switch
    {
        "g" => qty,
        "kg" => qty * 1000,
        "ml" => qty,          // approximation 1 g/ml
        "cl" => qty * 10,
        "L" => qty * 1000,
        "c. à café" => qty * 5,
        "c. à soupe" => qty * 15,
        "pincée" => qty * 0.5,
        "verre" => qty * 200,
        "tasse" => qty * 150,
        "bol" => qty * 350,
        "pot" => qty * (unitWeightG ?? 125),
        _ => unitWeightG is double w ? qty * w : null, // pièce(s), tranche(s), poignée, portion…
    };

    // ── Réorganisation manuelle (SortOrder) ─────────────────────────────────
    // Chaque déplacement normalise d'abord 0..n-1 dans l'ordre d'affichage actuel
    // (SortOrder, CreatedAt, Id) : les données antérieures à la colonne (tout à 0)
    // gardent leur ordre, puis deviennent réorganisables.

    public async Task MoveFoodEntryAsync(int foodId, int delta)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var food = await ctx.FoodEntries.FindAsync(foodId);
        if (food == null) return;
        var all = await OrderedFoodsAsync(ctx, food.DayEventId);
        // Le déplacement se fait entre voisins visibles : même préparation (ou hors préparation).
        var scope = all.Where(f => (f.GroupName ?? "") == (food.GroupName ?? "")).ToList();
        var idx = scope.FindIndex(f => f.Id == foodId);
        var tgt = idx + delta;
        if (idx < 0 || tgt < 0 || tgt >= scope.Count) return;
        (scope[idx].SortOrder, scope[tgt].SortOrder) = (scope[tgt].SortOrder, scope[idx].SortOrder);
        await ctx.SaveChangesAsync();
    }

    // Dépôt d'un aliment sur un autre : il prend sa position (et rejoint sa préparation).
    public async Task MoveFoodBeforeAsync(int foodId, int targetFoodId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var food = await ctx.FoodEntries.FindAsync(foodId);
        var target = await ctx.FoodEntries.FindAsync(targetFoodId);
        if (food == null || target == null || food.DayEventId != target.DayEventId || foodId == targetFoodId) return;
        var all = await OrderedFoodsAsync(ctx, food.DayEventId);
        food.GroupName = target.GroupName;
        var list = all.Where(f => f.Id != foodId).ToList();
        list.Insert(list.FindIndex(f => f.Id == targetFoodId), food);
        for (var i = 0; i < list.Count; i++) list[i].SortOrder = i;
        await ctx.SaveChangesAsync();
    }

    public async Task MovePhotoAsync(int photoId, int delta)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var photo = await ctx.MealPhotos.FindAsync(photoId);
        if (photo == null) return;
        var all = await ctx.MealPhotos.Where(p => p.DayEventId == photo.DayEventId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync();
        for (var i = 0; i < all.Count; i++) all[i].SortOrder = i;
        var idx = all.FindIndex(p => p.Id == photoId);
        var tgt = idx + delta;
        if (idx < 0 || tgt < 0 || tgt >= all.Count) return;
        (all[idx].SortOrder, all[tgt].SortOrder) = (all[tgt].SortOrder, all[idx].SortOrder);
        await ctx.SaveChangesAsync();
    }

    // Déplace un groupe de boissons entier (les chips agrègent les entrées par nom).
    public async Task MoveDrinkGroupAsync(int dayEventId, string drinkName, int delta)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var all = await ctx.DrinkEntries.Where(d => d.DayEventId == dayEventId)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync();
        var groups = all.GroupBy(d => d.DrinkName).Select(g => g.ToList()).ToList();
        var idx = groups.FindIndex(g => g[0].DrinkName == drinkName);
        var tgt = idx + delta;
        if (idx < 0 || tgt < 0 || tgt >= groups.Count) return;
        (groups[idx], groups[tgt]) = (groups[tgt], groups[idx]);
        var seq = 0;
        foreach (var entry in groups.SelectMany(g => g)) entry.SortOrder = seq++;
        await ctx.SaveChangesAsync();
    }

    static async Task<List<FoodEntry>> OrderedFoodsAsync(NutriPlanContext ctx, int dayEventId)
    {
        var all = await ctx.FoodEntries.Where(f => f.DayEventId == dayEventId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync();
        for (var i = 0; i < all.Count; i++) all[i].SortOrder = i;
        return all;
    }

    public async Task<DrinkEntry> AddDrinkEntryAsync(int dayEventId, string drinkName)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = new DrinkEntry
        {
            DayEventId = dayEventId,
            DrinkName = drinkName,
            CreatedAt = DateTime.UtcNow,
            SortOrder = ((await ctx.DrinkEntries.Where(d => d.DayEventId == dayEventId).MaxAsync(d => (int?)d.SortOrder)) ?? -1) + 1
        };
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

    public async Task UpdateFoodEntryAsync(int entryId, string foodName, string? quantity, string? quantityUnit, string? groupName)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var entry = await ctx.FoodEntries.FindAsync(entryId);
        if (entry == null) return;
        entry.FoodName = foodName;
        entry.Quantity = string.IsNullOrWhiteSpace(quantity) ? null : quantity.Trim();
        entry.QuantityUnit = string.IsNullOrWhiteSpace(quantityUnit) ? null : quantityUnit.Trim();
        entry.GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        await ctx.SaveChangesAsync();
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
