using System.Text;
using System.Text.Json;

namespace NutriPlan.Services;

// Analyse de phrase par IA (optionnelle) pour la saisie express.
// Le fournisseur (OpenAI, Azure OpenAI ou Claude API) et sa clé sont configurés
// dans le backend admin ; sans configuration — ou au moindre échec — l'appelant
// replie sur l'analyse locale MealPhraseParser (gratuite, instantanée).
// L'IA ne reçoit que la phrase du repas : jamais d'identité ni d'historique.
public class AiMealParserService(SettingsService settings, IHttpClientFactory httpFactory)
{
    const string SystemPrompt =
        """
        Tu extrais d'une phrase en français la liste des aliments consommés.
        Réponds UNIQUEMENT avec un objet JSON strict, sans texte autour :
        {"slot":entier|null,"time":"HH:mm"|null,"items":[{"name":"...","qty":nombre|null,"unit":"..."|null,"bio":true|false}]}
        slot : 0=petit-déjeuner, 1=collation matin, 2=déjeuner, 3=collation après-midi, 4=dîner, 5=collation soir ; null si la phrase ne le précise pas.
        time : heure citée explicitement (« à 12h30 »), sinon null.
        name : nom générique de l'aliment, sans quantité ni mention bio (ex. « riz complet », « œuf »).
        qty : nombre (0.5 pour une moitié), null si non précisé.
        unit : parmi "g","kg","ml","cl","L","c. à café","c. à soupe","verre","tasse","bol","pot","pincée","tranche(s)","part(s)","portion","morceau(x)","boule(s)","poignée","assiette","pièce(s)","filet(s)" — ou null pour l'unité naturelle de l'aliment.
        bio : true seulement si la phrase le dit.
        """;

    static readonly HashSet<string> ValidUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "g", "kg", "ml", "cl", "L", "c. à café", "c. à soupe", "verre", "tasse",
        "bol", "pot", "pincée", "tranche(s)", "part(s)", "portion", "morceau(x)",
        "boule(s)", "poignée", "assiette", "pièce(s)", "filet(s)",
    };

    sealed record AiItem(string? Name, double? Qty, string? Unit, bool Bio);
    sealed record AiMeal(int? Slot, string? Time, List<AiItem>? Items);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<bool> IsConfiguredAsync()
    {
        var (provider, apiKey, model, endpoint) = await settings.GetAiConfigAsync();
        return provider switch
        {
            "openai" or "anthropic" or "openrouter" => apiKey != "" && model != "",
            "azure" => apiKey != "" && model != "" && endpoint != "",
            _ => false,
        };
    }

    /// <summary>Analyse par le fournisseur configuré ; null si non configuré ou en échec (→ repli local).</summary>
    public async Task<MealPhraseParser.ParsedMeal?> ParseAsync(string phrase)
    {
        try
        {
            var raw = await CallProviderAsync(phrase);
            return raw == null ? null : MapToParsedMeal(raw);
        }
        catch
        {
            return null; // l'appelant replie sur l'analyse locale
        }
    }

    /// <summary>Appel de vérification pour le backend admin (phrase d'exemple fixe).</summary>
    public async Task<(bool Ok, string Message)> TestAsync()
    {
        if (!await IsConfiguredAsync()) return (false, "Configuration incomplète (fournisseur, clé, modèle…).");
        try
        {
            var meal = await ParseAsync("À midi j'ai mangé 100 g de riz complet et deux œufs bio");
            if (meal == null || meal.Items.Count == 0)
                return (false, "Le fournisseur a répondu mais sans extraction exploitable.");
            var names = string.Join(", ", meal.Items.Select(i => i.Query));
            return (true, $"Extraction réussie : {meal.Items.Count} aliments ({names}).");
        }
        catch (Exception ex)
        {
            return (false, $"Échec de l'appel : {ex.Message}");
        }
    }

    async Task<string?> CallProviderAsync(string phrase)
    {
        var (provider, apiKey, model, endpoint) = await settings.GetAiConfigAsync();
        var http = httpFactory.CreateClient("ai");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        HttpRequestMessage req;
        if (provider == "anthropic")
        {
            req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = JsonContent(new
            {
                model,
                max_tokens = 3000, // la réflexion interne du modèle compte dans ce plafond
                system = SystemPrompt,
                messages = new[] { new { role = "user", content = phrase } },
            });
        }
        else if (provider is "openai" or "azure" or "openrouter")
        {
            // Dialecte « chat/completions » partagé : OpenAI, Azure OpenAI
            // (déploiement dans l'URL, modèle omis) et OpenRouter (compatible
            // OpenAI, modèles préfixés « éditeur/modèle »).
            var url = provider switch
            {
                "openai" => "https://api.openai.com/v1/chat/completions",
                "openrouter" => "https://openrouter.ai/api/v1/chat/completions",
                _ => $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(model)}/chat/completions?api-version=2024-10-21",
            };
            req = new HttpRequestMessage(HttpMethod.Post, url);
            if (provider == "azure") req.Headers.Add("api-key", apiKey);
            else req.Headers.Add("Authorization", $"Bearer {apiKey}");
            var messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = phrase },
            };
            object body = provider == "azure"
                ? new { messages, response_format = new { type = "json_object" } }
                : new { model, messages, response_format = new { type = "json_object" } };
            req.Content = JsonContent(body);
        }
        else return null;

        using var resp = await http.SendAsync(req, cts.Token);
        var payload = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {Truncate(payload, 180)}");

        using var doc = JsonDocument.Parse(payload);
        if (provider == "anthropic")
        {
            // Premier bloc de contenu de type "text" (les blocs "thinking" sont ignorés).
            foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                if (block.GetProperty("type").GetString() == "text")
                    return block.GetProperty("text").GetString();
            return null;
        }
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    static MealPhraseParser.ParsedMeal? MapToParsedMeal(string raw)
    {
        // Tolère les clôtures de code que certains modèles ajoutent malgré tout.
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var start = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) raw = raw[(start + 1)..end].Trim();
        }

        var meal = JsonSerializer.Deserialize<AiMeal>(raw, JsonOpts);
        if (meal?.Items == null) return null;

        TimeOnly? time = TimeOnly.TryParse(meal.Time ?? "", out var t) ? t : null;
        int? slot = meal.Slot is >= 0 and <= 5 ? meal.Slot : null;
        slot ??= time is TimeOnly tt ? MealPhraseParser.NearestSlot(tt) : null;

        var items = new List<MealPhraseParser.ParsedItem>();
        foreach (var it in meal.Items)
        {
            var name = (it.Name ?? "").Trim();
            if (name.Length < 2) continue;
            var unit = it.Unit != null && ValidUnits.TryGetValue(it.Unit.Trim(), out var canon) ? canon : null;
            items.Add(new MealPhraseParser.ParsedItem(name, name, it.Qty, unit, it.Bio));
        }
        return new MealPhraseParser.ParsedMeal(slot, time, items);
    }
}
