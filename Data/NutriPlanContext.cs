using Microsoft.EntityFrameworkCore;
using NutriPlan.Data.Models;

namespace NutriPlan.Data;

public class NutriPlanContext(DbContextOptions<NutriPlanContext> options) : DbContext(options)
{
    public DbSet<Nutritionist> Nutritionists => Set<Nutritionist>();
    public DbSet<FoodDiary> FoodDiaries => Set<FoodDiary>();
    public DbSet<DailyLog> DailyLogs => Set<DailyLog>();
    public DbSet<MealLog> MealLogs => Set<MealLog>();
    public DbSet<FoodEntry> FoodEntries => Set<FoodEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FoodDiary>()
            .HasIndex(d => d.AccessCode)
            .IsUnique();

        modelBuilder.Entity<DailyLog>()
            .HasIndex(l => new { l.FoodDiaryId, l.Date })
            .IsUnique();

        modelBuilder.Entity<Nutritionist>()
            .HasIndex(n => n.Email)
            .IsUnique();

        modelBuilder.Entity<MealLog>()
            .HasMany(m => m.FoodEntries)
            .WithOne(e => e.MealLog)
            .HasForeignKey(e => e.MealLogId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DailyLog>()
            .HasMany(d => d.MealLogs)
            .WithOne(m => m.DailyLog)
            .HasForeignKey(m => m.DailyLogId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
