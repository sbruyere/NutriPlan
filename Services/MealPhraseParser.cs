using System.Text.RegularExpressions;

namespace NutriPlan.Services;

// Agent de saisie express : transforme une phrase libre (dictée ou tapée)
// en éléments structurés prêts pour la recherche dans le référentiel.
//   « À midi j'ai mangé 150 g de riz complet bio avec deux œufs et une tomate »
//   → créneau Déjeuner + [riz complet (150 g, bio), œufs (2), tomate (1)]
// Aucune IA : découpage par connecteurs, extraction des quantités/unités/bio
// par motifs — coût nul, résultat instantané, hors-ligne.
public static partial class MealPhraseParser
{
    public sealed record ParsedItem(
        string Raw,          // segment d'origine (affichage)
        string Query,        // requête nettoyée pour la recherche catalogue
        double? Qty,         // quantité numérique si repérée
        string? Unit,        // unité canonique (vocabulaire de DiaryService.ToGrams) ; null = unité naturelle
        bool IsBio);

    public sealed record ParsedMeal(int? SlotIndex, TimeOnly? ExplicitTime, List<ParsedItem> Items);

    // ── Créneaux : mots-clés pliés → index de DiaryService.MealSlots ────────
    static readonly (string Needle, int Slot)[] SlotKeywords =
    [
        ("petit dejeuner", 0), ("petit dej", 0), ("au reveil", 0), ("au petit déj", 0),
        ("collation du matin", 1), ("collation matin", 1), ("en cas du matin", 1), ("dans la matinee", 1),
        ("a midi", 2), ("ce midi", 2), ("au dejeuner", 2), ("dejeuner", 2),
        ("gouter", 3), ("quatre heures", 3), ("collation de l apres midi", 3),
        ("collation apres midi", 3), ("dans l apres midi", 3),
        ("au diner", 4), ("diner", 4), ("ce soir", 4), ("souper", 4),
        ("collation du soir", 5), ("avant de dormir", 5), ("avant de me coucher", 5), ("en soiree", 5),
    ];

    // Tournures d'introduction à retirer avant le découpage.
    [GeneratedRegex(@"\b(j ?ai (mange|pris|bu|avale|consomme)|nous avons mange|on a (mange|pris)|je (mange|prends)|c etait|il y avait|pour (commencer|finir))\b", RegexOptions.CultureInvariant)]
    private static partial Regex IntroRegex();

    // « à 12h30 », « a 9 h », « vers 19h »
    [GeneratedRegex(@"\b(?:a|vers)\s+(\d{1,2})\s*h(?:\s*(\d{2}))?\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    // Connecteurs entre aliments.
    [GeneratedRegex(@"\s*(?:,|;|\bet\b|\bavec\b|\bpuis\b|\bainsi que\b|\baccompagnee? de\b|\baccompagnee? d\b)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SplitRegex();

    // Quantité pesée/versée : « 150 g », « 1,5 l », « 25cl »…
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(kg|kilos?|grammes?|gr|g|ml|cl|litres?|l)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WeightRegex();

    // Nombres en toutes lettres.
    static readonly Dictionary<string, double> NumberWords = new()
    {
        ["un"] = 1, ["une"] = 1, ["deux"] = 2, ["trois"] = 3, ["quatre"] = 4,
        ["cinq"] = 5, ["six"] = 6, ["sept"] = 7, ["huit"] = 8, ["neuf"] = 9,
        ["dix"] = 10, ["onze"] = 11, ["douze"] = 12, ["quinze"] = 15, ["vingt"] = 20,
        ["demi"] = 0.5, ["demie"] = 0.5, ["dizaine"] = 10, ["douzaine"] = 12, ["quinzaine"] = 15,
        ["quelques"] = 3, ["plusieurs"] = 3,
    };

    // Unités « ustensiles » pliées → unité canonique de DiaryService.ToGrams.
    static readonly (string Needle, string Unit)[] UtensilUnits =
    [
        ("cuilleres a soupe", "c. à soupe"), ("cuillere a soupe", "c. à soupe"),
        ("cuilleres a cafe", "c. à café"), ("cuillere a cafe", "c. à café"),
        ("c a s", "c. à soupe"), ("cas de", "c. à soupe"),
        ("c a c", "c. à café"), ("cac de", "c. à café"),
        ("verres", "verre"), ("verre", "verre"),
        ("bols", "bol"), ("bol", "bol"),
        ("tasses", "tasse"), ("tasse", "tasse"),
        ("pincees", "pincée"), ("pincee", "pincée"),
        ("pots", "pot"), ("pot", "pot"),
        ("tranches", "tranche(s)"), ("tranche", "tranche(s)"),
        ("parts", "part(s)"), ("part", "part(s)"),
        ("portions", "portion"), ("portion", "portion"),
        ("morceaux", "morceau(x)"), ("morceau", "morceau(x)"),
        ("boules", "boule(s)"), ("boule", "boule(s)"),
        ("poignees", "poignée"), ("poignee", "poignée"),
        ("assiettes", "assiette"), ("assiette", "assiette"),
        ("filets", "filet(s)"), ("filet", "filet(s)"),
        ("steaks", "pièce(s)"), ("steak", "pièce(s)"),
    ];

    // Mots vides en tête de requête (« de la salade » → « salade »).
    [GeneratedRegex(@"^(?:de la |de l |du |des |de |d |la |le |les |l |un |une |mon |ma |mes )+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingStopWords();

    [GeneratedRegex(@"\b(bio|biologiques?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BioRegex();

    public static ParsedMeal Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ParsedMeal(null, null, []);

        // Tout le traitement se fait sur le texte plié : la recherche catalogue
        // plie elle aussi sa requête, rien n'est perdu.
        var folded = FoodSearch.Fold(text).Replace('’', ' ').Replace('\'', ' ');
        folded = Regex.Replace(folded, @"\s+", " ").Trim();

        // 1. Créneau explicite (le plus long motif d'abord : « petit dejeuner »
        //    doit gagner sur « dejeuner »).
        int? slot = null;
        foreach (var (needle, s) in SlotKeywords.OrderByDescending(k => k.Needle.Length))
        {
            if (folded.Contains(needle))
            {
                slot = s;
                folded = folded.Replace(needle, " ");
                break;
            }
        }

        // 2. Heure explicite « à 12h30 » → heure + créneau le plus proche.
        TimeOnly? explicitTime = null;
        var tm = TimeRegex().Match(folded);
        if (tm.Success && int.TryParse(tm.Groups[1].Value, out var hh) && hh < 24)
        {
            int mm = tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0;
            if (mm < 60)
            {
                explicitTime = new TimeOnly(hh, mm);
                slot ??= NearestSlot(explicitTime.Value);
                folded = folded.Remove(tm.Index, tm.Length);
            }
        }

        // 3. Tournures d'introduction, puis découpage en segments d'aliments.
        folded = IntroRegex().Replace(folded, " ");
        var items = new List<ParsedItem>();
        foreach (var rawSeg in SplitRegex().Split(folded))
        {
            var item = ParseSegment(rawSeg);
            if (item != null) items.Add(item);
        }
        return new ParsedMeal(slot, explicitTime, items);
    }

    public static int NearestSlot(TimeOnly t)
    {
        int best = 0; double bestDist = double.MaxValue;
        for (int i = 0; i < DiaryService.MealSlots.Length; i++)
        {
            var d = Math.Abs((t - DiaryService.MealSlots[i].DefaultTime).TotalMinutes);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    static ParsedItem? ParseSegment(string seg)
    {
        seg = seg.Trim();
        if (seg.Length < 2) return null;
        var work = " " + seg + " ";

        // Bio — retiré de la requête, conservé en attribut.
        bool isBio = BioRegex().IsMatch(work);
        if (isBio) work = BioRegex().Replace(work, " ");

        double? qty = null;
        string? unit = null;

        // a) Poids/volume chiffré : « 150 g », « 25 cl »…
        var wm = WeightRegex().Match(work);
        if (wm.Success)
        {
            qty = double.Parse(wm.Groups[1].Value.Replace(',', '.'),
                               System.Globalization.CultureInfo.InvariantCulture);
            unit = wm.Groups[2].Value switch
            {
                "kg" or "kilo" or "kilos" => "kg",
                "ml" => "ml",
                "cl" => "cl",
                "l" or "litre" or "litres" => "L",
                _ => "g",
            };
            work = work.Remove(wm.Index, wm.Length);
        }

        // b) Nombre en tête (chiffre ou lettres) : « 2 tranches de pain », « deux œufs ».
        if (qty == null)
        {
            var nm = Regex.Match(work, @"^\s*(?:(\d+(?:[.,]\d+)?)|([a-z]+))\s+");
            if (nm.Success)
            {
                if (nm.Groups[1].Success)
                {
                    qty = double.Parse(nm.Groups[1].Value.Replace(',', '.'),
                                       System.Globalization.CultureInfo.InvariantCulture);
                    work = work[nm.Length..];
                }
                else if (NumberWords.TryGetValue(nm.Groups[2].Value, out var n))
                {
                    qty = n;
                    work = work[nm.Length..];
                    // « une demi baguette », « un demi avocat » → 0,5.
                    var half = Regex.Match(work, @"^\s*demie?s?\s+");
                    if (half.Success && qty == 1) { qty = 0.5; work = work[half.Length..]; }
                }
            }
        }

        // c) Unité « ustensile » : « verre de », « tranches de »…
        work = " " + work.Trim() + " ";
        foreach (var (needle, u) in UtensilUnits)
        {
            var idx = work.IndexOf(" " + needle + " ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                unit ??= u;
                qty ??= 1;
                work = work.Remove(idx, needle.Length + 1);
                break;
            }
        }

        // d) Requête restante : sans articles de tête ni résidus.
        var query = Regex.Replace(work, @"\s+", " ").Trim();
        query = LeadingStopWords().Replace(query, "");
        if (query.Length < 2) return null;

        return new ParsedItem(seg.Trim(), query, qty, unit, isBio);
    }
}
