using AnteraApp.Api.Models;
using System.Globalization;
using System.Text;

namespace AnteraApp.Api.Services;

public static class PollenAllergyLevelClassifier
{
    private sealed record AllergyLevelScale(double LowMaximum, double MediumMaximum, string Description);

    private static readonly AllergyLevelScale Group1 = new(
        15,
        30,
        "Se consideran valores bajos hasta 15 gr/m³, medios entre 16 y 30 gr/m³ y altos desde 31 gr/m³.");
    private static readonly AllergyLevelScale Group2 = new(
        25,
        50,
        "Se consideran valores bajos hasta 25 gr/m³, medios entre 26 y 50 gr/m³ y altos desde 51 gr/m³.");
    private static readonly AllergyLevelScale Group2Low = new(
        10,
        20,
        "Se consideran valores bajos hasta 10 gr/m³, medios entre 11 y 20 gr/m³ y altos desde 21 gr/m³.");
    private static readonly AllergyLevelScale Group2Grass = new(
        10,
        50,
        "Se consideran valores bajos hasta 10 gr/m³, medios entre 11 y 50 gr/m³ y altos desde 51 gr/m³.");
    private static readonly AllergyLevelScale Group3 = new(
        30,
        50,
        "Se consideran valores bajos hasta 30 gr/m³, medios entre 31 y 50 gr/m³ y altos desde 51 gr/m³.");
    private static readonly AllergyLevelScale Group4 = new(
        49.999,
        200,
        "Se consideran valores bajos por debajo de 50 gr/m³, medios entre 50 y 200 gr/m³ y altos por encima de 200 gr/m³.");

    // Umbrales de la Red Española de Aerobiología (REA), expresados en granos/m³ de 24 horas.
    public static IReadOnlyList<PollenSpeciesDto> Classify(IEnumerable<PollenSpeciesDto> species) =>
        species.Select(Classify).ToArray();

    private static PollenSpeciesDto Classify(PollenSpeciesDto species) =>
        species with
        {
            AllergyLevel = GetAllergyLevel(species.Name, species.Value, species.Unit),
            AllergyLevelScale = GetAllergyLevelScale(species.Name, species.Unit)
        };

    private static string? GetAllergyLevel(string name, double? value, string unit)
    {
        if (value is null || !string.Equals(unit, "granos/m³", StringComparison.Ordinal))
        {
            return null;
        }

        var scale = GetScale(Normalize(name));
        return value.Value <= scale.LowMaximum ? "Bajo" :
            value.Value <= scale.MediumMaximum ? "Medio" :
            "Alto";
    }

    private static string? GetAllergyLevelScale(string name, string unit)
    {
        if (string.Equals(unit, "nivel regional", StringComparison.Ordinal))
        {
            return "La XAC usa una escala cualitativa: 0 Nulo, 1 Bajo, 2 Medio, 3 Alto y 4 Máximo; no publica concentración en gr/m³.";
        }

        if (string.Equals(unit, "nivel", StringComparison.Ordinal))
        {
            return "La red regional publica un nivel cualitativo propio; Antera lo muestra sin convertirlo a gr/m³.";
        }

        return string.Equals(unit, "granos/m³", StringComparison.Ordinal)
            ? GetScale(Normalize(name)).Description
            : null;
    }

    private static AllergyLevelScale GetScale(string normalizedName) => normalizedName switch
    {
        var item when ContainsAny(item, "viborera", "echium", "mercurial", "mercurialis", "melcoratge", "malcoratge") => Group1,
        var item when ContainsAny(item, "acedera", "rumex", "artemisa", "artemisia") => Group2,
        var item when ContainsAny(item, "ambrosia", "ragweed", "margaritas", "asteraceas", "diente de leon", "taraxacum", "girasol", "helianthus", "brezo", "ericaceae", "rosaceas", "rosaceae") => Group2,
        var item when ContainsAny(item, "cenigos", "cenizos", "cenizo", "blets", "amarantaceas", "amaranthaceae", "chenopodiaceae", "ortiga", "parietaria", "urticaceae") => Group2Low,
        var item when ContainsAny(item, "llanten", "plantago", "gramineas", "poaceae", "grass") => Group2Grass,
        var item when ContainsAny(item, "castano", "castanyer", "castanea", "chopo", "alamo", "populus", "moral", "morus", "sauce", "salix", "tilo", "tilia", "eucalipto", "myrtaceae", "aligustre", "ligustrum", "aliso", "alnus", "abedul", "betula", "fresno", "fraxinus", "haya", "faig", "fagus") => Group3,
        var item when ContainsAny(item, "cipres", "cupressaceae", "cupresaceas", "olivo", "olea", "pino", "pinus", "platano", "platanus", "encina", "roble", "quercus") => Group4,
        _ => Group2
    };

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }
}
