using Microsoft.EntityFrameworkCore;
using NutriPlan.Components;
using NutriPlan.Data;
using NutriPlan.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Stockage persistant ─────────────────────────────────────────────────────
// Azure App Service REMPLACE le dossier de déploiement à chaque publication :
// tout fichier absent du paquet (base SQLite, photos) y serait supprimé.
// %HOME%\data survit aux déploiements → la base et les uploads y vivent en prod.
// En local, on garde le dossier du projet et wwwroot/uploads comme avant.
var isAzure = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") != null
              && Environment.GetEnvironmentVariable("HOME") is not null;
var dataDir = isAzure
    ? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "data", "nutriplan")
    : builder.Environment.ContentRootPath;
var uploadsDir = isAzure
    ? Path.Combine(dataDir, "uploads")
    : Path.Combine(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"), "uploads");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(uploadsDir);
NutriPlan.Services.AppPaths.UploadsDir = uploadsDir;

builder.Services.AddDbContextFactory<NutriPlanContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? $"Data Source={Path.Combine(dataDir, "nutriplan.db")}"));

builder.Services.AddScoped<DiarySessionService>();
builder.Services.AddScoped<DiaryService>();
builder.Services.AddScoped<NutritionistService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BrowserTimeService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NutriPlanContext>>();
    using var ctx = factory.CreateDbContext();
    ctx.Database.EnsureCreated();
    // Ajout de la colonne QuantityUnit si elle n'existe pas (SQLite ne supporte pas IF NOT EXISTS sur ALTER COLUMN).
    // Seule l'erreur « colonne déjà présente » est absorbée : tout autre échec (fichier verrouillé,
    // base en lecture seule…) doit faire échouer le démarrage plutôt que casser le schéma en silence.
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE FoodEntries ADD COLUMN QuantityUnit TEXT;"); }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    // Ordre d'affichage réorganisable des aliments / boissons / photos.
    foreach (var alterSql in new[]
    {
        "ALTER TABLE FoodEntries ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE DrinkEntries ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;",
        "ALTER TABLE MealPhotos ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;",
    })
    {
        try { ctx.Database.ExecuteSqlRaw(alterSql); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }
    // Lien FoodEntry → référentiel Ciqual.
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE FoodEntries ADD COLUMN CatalogFoodId INTEGER;"); }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    // Détails d'activité sportive + profil du carnet (métabolisme/objectifs).
    foreach (var actCol in new[]
    {
        "ALTER TABLE DayEvents ADD COLUMN CaloriesBurned INTEGER;",
        "ALTER TABLE DayEvents ADD COLUMN DistanceKm REAL;",
        "ALTER TABLE DayEvents ADD COLUMN ElevationM INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN Sex TEXT;",
        "ALTER TABLE FoodDiaries ADD COLUMN HeightCm INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN WeightKg REAL;",
        "ALTER TABLE FoodDiaries ADD COLUMN BirthYear INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN ActivityLevel TEXT;",
        "ALTER TABLE FoodDiaries ADD COLUMN DailyKcalTarget INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN DailyBurnTarget INTEGER;",
    })
    {
        try { ctx.Database.ExecuteSqlRaw(actCol); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }

    // Référentiel d'aliments Ciqual : la table n'existe pas sur les bases créées avant
    // son introduction (EnsureCreated ne fait pas évoluer un schéma existant).
    ctx.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "CatalogFoods" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CatalogFoods" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "SearchName" TEXT NOT NULL,
            "GroupCode" TEXT NOT NULL,
            "SubGroupCode" TEXT NOT NULL,
            "SubGroupName" TEXT NULL,
            "KcalPer100g" REAL NULL,
            "ProteinPer100g" REAL NULL,
            "CarbsPer100g" REAL NULL,
            "SugarsPer100g" REAL NULL,
            "FatPer100g" REAL NULL,
            "SatFatPer100g" REAL NULL,
            "FiberPer100g" REAL NULL,
            "SaltPer100g" REAL NULL,
            "DefaultUnit" TEXT NOT NULL,
            "Units" TEXT NOT NULL,
            "UnitWeightG" REAL NULL,
            "TypicalQty" REAL NULL);
        """);
    ctx.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_CatalogFoods_SearchName\" ON \"CatalogFoods\" (\"SearchName\");");
    // Macronutriments ajoutés après la première version du référentiel.
    foreach (var macroCol in new[] { "ProteinPer100g", "CarbsPer100g", "SugarsPer100g", "FatPer100g", "SatFatPer100g", "FiberPer100g", "SaltPer100g" })
    {
        try { ctx.Database.ExecuteSqlRaw($"ALTER TABLE CatalogFoods ADD COLUMN {macroCol} REAL;"); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }

    // Seed au premier démarrage — ou re-seed si le référentiel date d'avant les
    // macronutriments (aucune ligne n'a de protéines : impossible sur un seed à jour).
    if (!ctx.CatalogFoods.Any(c => c.ProteinPer100g != null))
    {
        var seedPath = Path.Combine(app.Environment.ContentRootPath, "Data", "Seed", "food-catalog.json");
        if (File.Exists(seedPath))
        {
            var seed = System.Text.Json.JsonSerializer.Deserialize<List<NutriPlan.Data.Models.CatalogFood>>(File.ReadAllText(seedPath));
            if (seed is { Count: > 0 })
            {
                ctx.CatalogFoods.ExecuteDelete();
                ctx.CatalogFoods.AddRange(seed);
                ctx.SaveChanges();
            }
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Les photos uploadées vivent hors du dossier de déploiement (stockage persistant)
// et sont créées au runtime : MapStaticAssets ne les sert pas, ce middleware oui.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads",
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
