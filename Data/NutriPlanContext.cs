using Microsoft.EntityFrameworkCore;
using NutriPlan.Data.Models;

namespace NutriPlan.Data;

public class NutriPlanContext(DbContextOptions<NutriPlanContext> options) : DbContext(options)
{
    public DbSet<Nutritionist> Nutritionists => Set<Nutritionist>();
    public DbSet<FoodDiary> FoodDiaries => Set<FoodDiary>();
    public DbSet<DailyLog> DailyLogs => Set<DailyLog>();
    public DbSet<DayEvent> DayEvents => Set<DayEvent>();
    public DbSet<FoodEntry> FoodEntries => Set<FoodEntry>();
    public DbSet<DrinkEntry> DrinkEntries => Set<DrinkEntry>();
    public DbSet<MealPhoto> MealPhotos => Set<MealPhoto>();
    public DbSet<CatalogFood> CatalogFoods => Set<CatalogFood>();
    public DbSet<WeightEntry> WeightEntries => Set<WeightEntry>();
    public DbSet<DayComment> DayComments => Set<DayComment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FoodDiary>().HasIndex(d => d.AccessCode).IsUnique();
        modelBuilder.Entity<CatalogFood>().HasIndex(c => c.SearchName);
        modelBuilder.Entity<DailyLog>().HasIndex(l => new { l.FoodDiaryId, l.Date }).IsUnique();
        modelBuilder.Entity<Nutritionist>().HasIndex(n => n.Email).IsUnique();

        modelBuilder.Entity<DayEvent>()
            .HasMany(e => e.FoodEntries)
            .WithOne(f => f.DayEvent)
            .HasForeignKey(f => f.DayEventId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DayEvent>()
            .HasMany(e => e.DrinkEntries)
            .WithOne(d => d.DayEvent)
            .HasForeignKey(d => d.DayEventId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DayEvent>()
            .HasMany(e => e.Photos)
            .WithOne(p => p.DayEvent)
            .HasForeignKey(p => p.DayEventId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DailyLog>()
            .HasMany(d => d.Events)
            .WithOne(e => e.DailyLog)
            .HasForeignKey(e => e.DailyLogId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DailyLog>()
            .HasMany(d => d.Comments)
            .WithOne(c => c.DailyLog)
            .HasForeignKey(c => c.DailyLogId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FoodDiary>()
            .HasMany(d => d.WeightEntries)
            .WithOne(w => w.FoodDiary)
            .HasForeignKey(w => w.FoodDiaryId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<WeightEntry>().HasIndex(w => new { w.FoodDiaryId, w.Date }).IsUnique();
    }
}
