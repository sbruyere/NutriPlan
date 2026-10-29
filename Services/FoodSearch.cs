using System.Globalization;
using System.Text;
using NutriPlan.Data.Models;

namespace NutriPlan.Services;

// Recherche d'aliments dans le référentiel : pliage accents/casse, tri par
// « pureté » (l'aliment brut avant la préparation qui le contient), tolérance
// au pluriel (« tomates » trouve « Tomate, crue »). Partagé entre
// l'autocomplétion (FoodAutocomplete) et la saisie express (MealPhraseParser).
public static class FoodSearch
{
    // Minuscules sans diacritiques — « creme » trouve « Crème fraîche ».
    // Les ligatures (œ, æ) ne sont pas décomposées par NFD : traitées à part,
    // sinon « œufs » ne trouverait jamais « Oeuf dur » (graphies mélangées).
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            switch (char.ToLowerInvariant(c))
            {
                case 'œ': sb.Append("oe"); break;
                case 'æ': sb.Append("ae"); break;
                default: sb.Append(char.ToLowerInvariant(c)); break;
            }
        }
        return sb.ToString();
    }

    // ── Tri par « pureté » ───────────────────────────────────────────────────
    // « Riz blanc, cuit » doit précéder « Salade de riz au thon…, préemballée ».
    // Marqueurs de transformation/composition repérés dans le nom plié.
    static readonly string[] ComposedMarkers =
    [
        "preemball", "appertis", "surgel", "cuisin", "du commerce", "rayon frais",
        "fait maison", "plat ", "salade de", "sauce", "farci", "pane", "nappe",
        "fourre", "aromatis", "enrichi", "assaisonn", "melange", "dessert",
        "biscuit", "snack", "barre ", "confiture", "compote", "infantile",
        // États déshydratés/concentrés : jamais ce qu'on veut dire par le nom nu
        // (« tomate » ≠ « Tomate, poudre ») — sans effet quand la requête les cite,
        // la pénalité s'appliquant alors uniformément à tous les candidats.
        "seche", "poudre", "concentre", "deshydrat", "lyophilis",
    ];

    public static int PurityScore(CatalogFood c)
    {
        var f = c.SearchName;
        int score = 0;
        foreach (var m in ComposedMarkers) if (f.Contains(m)) score += 4;
        score += f.Count(ch => ch == ',');   // chaque qualificatif éloigne de l'aliment brut
        score += f.Length / 24;              // les intitulés interminables en dernier
        // « cru » est un état de composition du référentiel, pas une assiette :
        // pour la viande, le poisson et les fruits de mer, on saisit la version
        // cuite — les variantes crues restent proposées, mais après.
        if (f.Contains(" cru"))
        {
            var sg = Fold(c.SubGroupName ?? "");
            if (sg.Contains("viande") || sg.Contains("poisson") || sg.Contains("mollusque")
                || sg.Contains("crustace") || sg.Contains("charcuterie") || sg.Contains("oeufs"))
                score += 4;
        }
        return score;
    }

    // Un token matche s'il apparaît dans le nom plié — sinon on retente au
    // singulier naïf (« tomates » → « tomate », « gateaux » → « gateau »).
    static bool TokenMatches(string folded, string token)
    {
        if (folded.Contains(token)) return true;
        if (token.Length > 3 && (token.EndsWith('s') || token.EndsWith('x')))
            return folded.Contains(token[..^1]);
        return false;
    }

    public static bool Matches(string foldedName, string[] tokens) =>
        tokens.All(t => TokenMatches(foldedName, t));

    public static string[] Tokenize(string query) =>
        Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static string Singular(string t) =>
        t.Length > 3 && (t.EndsWith('s') || t.EndsWith('x')) ? t[..^1] : t;

    // Meilleurs candidats pour une requête libre : nom commençant par le 1er mot
    // (singulier accepté : « œufs » trouve « Oeuf dur ») d'abord, puis match en
    // début de mot — un match en plein milieu (« oeuf » dans « boeuf ») passe en
    // dernier —, puis les aliments « purs » avant les préparations composées.
    public static List<CatalogFood> Rank(IReadOnlyList<CatalogFood> catalog, string query, int max)
    {
        var tokens = Tokenize(query);
        if (tokens.Length == 0) return [];
        var t0 = tokens[0];
        var t0s = Singular(t0);
        int Anchor(CatalogFood c)
        {
            var f = c.SearchName;
            if (f.StartsWith(t0) || f.StartsWith(t0s)) return 0;
            if (f.Contains(" " + t0) || f.Contains(" " + t0s)) return 1;
            return 2;
        }
        return catalog.Where(c => Matches(c.SearchName, tokens))
            .OrderBy(Anchor)
            .ThenBy(PurityScore)
            .ThenBy(c => c.Name.Length)
            .Take(max)
            .ToList();
    }
}
