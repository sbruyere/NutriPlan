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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(dataDir, "nutriplan.db")}";

builder.Services.AddDbContextFactory<NutriPlanContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddHttpClient();
builder.Services.AddScoped<DiarySessionService>();
builder.Services.AddScoped<DiaryService>();
builder.Services.AddScoped<NutriPlan.Services.StravaService>();
builder.Services.AddScoped<NutritionistService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<BrowserTimeService>();

var app = builder.Build();

// ── Sauvegarde quotidienne de la base ───────────────────────────────────────
// Filet de sécurité contre la corruption ou les mauvaises manipulations :
// une copie par jour au démarrage (API de backup SQLite — sûre même avec WAL),
// 7 sauvegardes conservées, AVANT toute migration de schéma. Un échec de
// sauvegarde ne doit jamais empêcher l'application de démarrer.
try
{
    var dbFile = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    if (File.Exists(dbFile))
    {
        var backupsDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupsDir);
        var backupFile = Path.Combine(backupsDir, $"nutriplan-{DateTime.UtcNow:yyyy-MM-dd}.db");
        if (!File.Exists(backupFile))
        {
            using (var source = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            using (var target = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupFile}"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            foreach (var oldBackup in Directory.GetFiles(backupsDir, "nutriplan-*.db").OrderByDescending(f => f).Skip(7))
                File.Delete(oldBackup);
            Console.WriteLine($"Sauvegarde quotidienne de la base : {backupFile}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Sauvegarde de la base impossible : {ex.Message}");
}

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
        "ALTER TABLE FoodDiaries ADD COLUMN StravaAthleteId INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN StravaAccessToken TEXT;",
        "ALTER TABLE FoodDiaries ADD COLUMN StravaRefreshToken TEXT;",
        "ALTER TABLE FoodDiaries ADD COLUMN StravaTokenExpiresAt INTEGER;",
        "ALTER TABLE FoodDiaries ADD COLUMN StravaLastSyncAt TEXT;",
        "ALTER TABLE DayEvents ADD COLUMN StravaActivityId INTEGER;",
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
            "TypicalQty" REAL NULL,
            "Source" TEXT NOT NULL DEFAULT 'ciqual');
        """);
    ctx.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_CatalogFoods_SearchName\" ON \"CatalogFoods\" (\"SearchName\");");
    // Macronutriments ajoutés après la première version du référentiel.
    foreach (var macroCol in new[] { "ProteinPer100g", "CarbsPer100g", "SugarsPer100g", "FatPer100g", "SatFatPer100g", "FiberPer100g", "SaltPer100g" })
    {
        try { ctx.Database.ExecuteSqlRaw($"ALTER TABLE CatalogFoods ADD COLUMN {macroCol} REAL;"); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }
    // Référentiel d'origine (ciqual | usda), ajouté avec le complément USDA.
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE CatalogFoods ADD COLUMN Source TEXT NOT NULL DEFAULT 'ciqual';"); }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }

    // Historique pondéral et commentaires du praticien : tables absentes des bases
    // créées avant leur introduction (mêmes FK/cascades que le modèle EF).
    ctx.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "WeightEntries" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WeightEntries" PRIMARY KEY AUTOINCREMENT,
            "FoodDiaryId" INTEGER NOT NULL,
            "Date" TEXT NOT NULL,
            "WeightKg" REAL NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_WeightEntries_FoodDiaries_FoodDiaryId" FOREIGN KEY ("FoodDiaryId") REFERENCES "FoodDiaries" ("Id") ON DELETE CASCADE);
        """);
    ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_WeightEntries_FoodDiaryId_Date\" ON \"WeightEntries\" (\"FoodDiaryId\", \"Date\");");
    ctx.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "DayComments" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_DayComments" PRIMARY KEY AUTOINCREMENT,
            "DailyLogId" INTEGER NOT NULL,
            "Text" TEXT NOT NULL,
            "AuthorName" TEXT NULL,
            "ReadByPatient" INTEGER NOT NULL,
            "PatientReaction" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_DayComments_DailyLogs_DailyLogId" FOREIGN KEY ("DailyLogId") REFERENCES "DailyLogs" ("Id") ON DELETE CASCADE);
        """);
    ctx.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_DayComments_DailyLogId\" ON \"DayComments\" (\"DailyLogId\");");
    // Colonnes ajoutées après l'introduction des tables ci-dessus.
    foreach (var lateCol in new[] { "DayComments|PatientReaction TEXT", "FoodDiaries|TargetWeightKg REAL",
                                    "DayEvents|AvgHeartRate INTEGER", "DayEvents|MaxHeartRate INTEGER", "DayEvents|SufferScore REAL" })
    {
        var parts = lateCol.Split('|');
        try { ctx.Database.ExecuteSqlRaw($"ALTER TABLE {parts[0]} ADD COLUMN {parts[1]};"); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }

    // Seed au premier démarrage — ou re-seed si le référentiel date d'avant les
    // macronutriments (aucune ligne avec protéines) ou d'avant le complément USDA
    // (aucune ligne usda) : impossible sur un seed à jour.
    if (!ctx.CatalogFoods.Any(c => c.ProteinPer100g != null) || !ctx.CatalogFoods.Any(c => c.Source == "usda"))
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

// ── Callback OAuth Strava : échange du code puis retour au journal ──────────
app.MapGet("/strava/callback", async (string? code, string? state, string? error, string? scope,
    NutriPlan.Services.StravaService strava) =>
{
    var accessCode = (state ?? "").Trim().ToUpperInvariant();
    if (string.IsNullOrEmpty(accessCode)) return Results.Redirect("/");

    // state « login » = « Se connecter avec Strava » depuis l'accueil (les codes
    // d'accès font 8 caractères : aucune collision possible).
    if (accessCode == "LOGIN")
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return Results.Redirect("/");
        var login = await strava.FindDiaryByStravaLoginAsync(code!);
        return Results.Redirect(login == null
            ? "/?strava=unknown"
            : $"/carnet/{login.Value.AccessCode}/journal?sat={login.Value.Ticket}");
    }

    string? ticket = null;
    if (string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code))
        ticket = await strava.ConnectAsync(accessCode, code);
    if (ticket == null)
        return Results.Redirect($"/carnet/{accessCode}/journal?strava=failed");
    // Strava renvoie le scope réellement accordé : sans activity:read_all (case
    // « activités privées » décochée), l'import sera partiel ET chaque nouvelle
    // demande de ce scope rouvrirait le consentement — on prévient l'utilisateur.
    var partial = scope != null && !scope.Contains("activity:read_all");
    return Results.Redirect($"/carnet/{accessCode}/journal?strava={(partial ? "partial" : "ok")}&sat={ticket}");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
