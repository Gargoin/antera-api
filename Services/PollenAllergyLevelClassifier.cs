using AnteraApp.Api.Models;
using System.Globalization;
using System.Text;

namespace AnteraApp.Api.Services;

public static class PollenAllergyLevelClassifier
{
    // Umbrales de la Red Española de Aerobiología (REA), expresados en granos/m³ de 24 horas.
    public static IReadOnlyList<PollenSpeciesDto> Classify(IEnumerable<PollenSpeciesDto> species) =>
        species.Select(Classify).ToArray();

    private static PollenSpeciesDto Classify(PollenSpeciesDto species) =>
        species with { AllergyLevel = GetAllergyLevel(species.Name, species.Value, species.Unit) };

    private static string? GetAllergyLevel(string name, double? value, string unit)
    {
        if (value is null || !string.Equals(unit, "granos/m³", StringComparison.Ordinal))
        {
            return null;
        }

        var normalizedName = Normalize(name);
        return normalizedName switch
        {
            // Grupo 1 REA: bajo 1-15, moderado 16-30 y alto desde 31 granos/m³.
            var item when ContainsAny(item, "viborera", "echium", "mercurial", "mercurialis") => FromThresholds(value.Value, 16, 31),

            // Grupo 2 REA: bajo 1-25, moderado 26-50 y alto desde 51 granos/m³.
            var item when ContainsAny(item, "acedera", "rumex") => FromThresholds(value.Value, 26, 51),
            var item when ContainsAny(item, "artemisa", "artemisia") => FromThresholds(value.Value, 26, 51),
            var item when ContainsAny(item, "ambrosia", "ragweed", "margaritas", "asteraceas", "diente de leon", "taraxacum", "girasol", "helianthus", "brezo", "ericaceae", "rosaceas", "rosaceae") => FromThresholds(value.Value, 26, 51),
            var item when ContainsAny(item, "amarantaceas", "amaranthaceae", "chenopodiaceae") => FromThresholds(value.Value, 10, 20),
            var item when ContainsAny(item, "llanten", "plantago", "gramineas", "poaceae", "grass") => FromThresholds(value.Value, 10, 50),

            // Grupo 3 REA: bajo 1-30, moderado 31-50 y alto desde 51 granos/m³.
            var item when ContainsAny(item, "castano", "castanea", "chopo", "alamo", "populus", "moral", "morus", "sauce", "salix", "tilo", "tilia", "eucalipto", "myrtaceae", "aligustre", "ligustrum") => FromThresholds(value.Value, 31, 51),
            var item when ContainsAny(item, "aliso", "alnus", "abedul", "betula", "fresno", "fraxinus") => FromThresholds(value.Value, 30, 50),

            // Grupo 4 REA: bajo por debajo de 50, moderado entre 50 y 200 y alto por encima de 200 granos/m³.
            var item when ContainsAny(item, "cipres", "cupressaceae", "cupresaceas") => FromThresholds(value.Value, 50, 200),
            var item when ContainsAny(item, "olivo", "olea") => FromThresholds(value.Value, 50, 200),
            var item when ContainsAny(item, "pino", "pinus") => FromThresholds(value.Value, 50, 200),
            var item when ContainsAny(item, "platano", "platanus") => FromThresholds(value.Value, 50, 200),
            var item when ContainsAny(item, "encina", "roble", "quercus") => FromThresholds(value.Value, 50, 200),
            var item when ContainsAny(item, "ortiga", "parietaria", "urticaceae") => FromThresholds(value.Value, 10, 20),
            _ => FromThresholds(value.Value, 26, 51)
        };
    }

    private static string FromThresholds(double value, double lowUpperLimit, double mediumUpperLimit) =>
        value < lowUpperLimit ? "Bajo" :
        value <= mediumUpperLimit ? "Medio" :
        "Alto";

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
