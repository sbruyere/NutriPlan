using Microsoft.EntityFrameworkCore;
using NutriPlan.Data;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

public class NutritionistService(IDbContextFactory<NutriPlanContext> factory)
{
    public async Task<Nutritionist?> AuthenticateAsync(string email, string password)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var nutritionist = await ctx.Nutritionists
            .FirstOrDefaultAsync(n => n.Email == email.ToLowerInvariant());
        if (nutritionist == null) return null;
        return BCrypt.Net.BCrypt.Verify(password, nutritionist.PasswordHash) ? nutritionist : null;
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(string name, string email, string password)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.Nutritionists.AnyAsync(n => n.Email == email.ToLowerInvariant()))
            return (false, "Un compte avec cet email existe déjà.");

        var nutritionist = new Nutritionist
        {
            Name = name,
            Email = email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };
        ctx.Nutritionists.Add(nutritionist);
        await ctx.SaveChangesAsync();
        return (true, null);
    }

    public async Task<Nutritionist?> GetByIdAsync(int id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Nutritionists.FindAsync(id);
    }

    public async Task<List<Nutritionist>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        await using var ctx = await factory.CreateDbContextAsync();
        var q = query.Trim().ToLowerInvariant();
        return await ctx.Nutritionists
            .Where(n => n.Name.ToLower().Contains(q) || n.Email.ToLower().Contains(q))
            .Take(5)
            .ToListAsync();
    }
}
