using AnteraApp.Api.Models;
using System.Globalization;
using System.Text;

namespace AnteraApp.Api.Services;

public static class PollenCatalog
{
    public static readonly IReadOnlyList<PollenTypeDto> Types =
    [
        new("aliso", "Aliso", ["Aliso", "Alnus"]),
        new("abedul", "Abedul", ["Abedul", "Betula"]),
        new("amarantaceas", "Amarantáceas", ["Amarantáceas", "Amaranthaceae", "Chenopodiaceae", "Cenizo"]),
        new("ambrosia", "Ambrosía", ["Ambrosía", "Ragweed"]),
        new("artemisa", "Artemisa", ["Artemisa", "Artemisia"]),
        new("asteraceas", "Margaritas", ["Margaritas", "Margarita", "Asteráceas", "Asteraceae"]),
        new("castano", "Castaño", ["Castaño", "Castanea"]),
        new("cipres", "Ciprés", ["Ciprés", "Cupressaceae", "Cupresáceas", "Cupráceas", "Enebro", "Sabina"]),
        new("chopo-alamo", "Chopo y álamo", ["Chopo", "Álamo", "Populus"]),
        new("diente-de-leon", "Diente de león", ["Diente de león", "Taraxacum"]),
        new("echium", "Viborera", ["Viborera", "Echium"]),
        new("encina-roble", "Encina y roble", ["Encina", "Roble", "Quercus"]),
        new("fresno", "Fresno", ["Fresno", "Fraxinus"]),
        new("girasol", "Girasol", ["Girasol", "Helianthus"]),
        new("gramineas", "Gramíneas", ["Gramíneas", "Poaceae", "Grass"]),
        new("llanten", "Llantén", ["Llantén", "Plantago"]),
        new("moral", "Moral", ["Moral", "Morus"]),
        new("olivo", "Olivo", ["Olivo", "Olea"]),
        new("ortiga-parietaria", "Ortiga y parietaria", ["Ortiga", "Parietaria", "Urticaceae"]),
        new("pino", "Pino", ["Pino", "Pinus"]),
        new("platano-sombra", "Plátano de sombra", ["Plátano de sombra", "Plátano de paseo", "Platanus"]),
        new("rosaceas", "Rosáceas", ["Rosáceas", "Rosaceae"]),
        new("sauce", "Sauce", ["Sauce", "Salix"]),
        new("tilo", "Tilo", ["Tilo", "Tilia"]),
        new("brezo", "Brezo", ["Brezo", "Ericaceae"]),
        new("aligustre", "Aligustre", ["Aligustre", "Ligustrum"]),
        new("eucalipto", "Eucalipto", ["Eucalipto", "Myrtaceae"]),
        new("mercurial", "Mercurial", ["Mercurial", "Mercurialis"]),
        new("acedera", "Acedera", ["Acedera", "Rumex"]),
    ];

    public static bool Contains(string pollenTypeId) =>
        Types.Any(type => string.Equals(type.Id, pollenTypeId, StringComparison.Ordinal));

    public static IReadOnlyList<PollenTypeDto> FromIds(IEnumerable<string> pollenTypeIds) =>
        pollenTypeIds
            .Select(id => Types.FirstOrDefault(type => string.Equals(type.Id, id, StringComparison.Ordinal)))
            .Where(type => type is not null)
            .Cast<PollenTypeDto>()
            .ToArray();

    /// <summary>
    /// Conserva únicamente especies que Antera identifica en su catálogo y les asigna
    /// la misma etiqueta pública que se usa en el selector de preferencias.
    /// </summary>
    public static IReadOnlyList<PollenSpeciesDto> NormalizeSpecies(IEnumerable<PollenSpeciesDto> species) =>
        species
            .Select(species => TryGetCanonicalName(species.Name, out var canonicalName)
                ? species with { Name = canonicalName }
                : null)
            .Where(species => species is not null)
            .Cast<PollenSpeciesDto>()
            .GroupBy(species => species.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(species => species.Value is not null)
                .ThenByDescending(species => species.Value)
                .First())
            .ToArray();

    private static bool TryGetCanonicalName(string name, out string canonicalName)
    {
        var normalizedName = Normalize(name);
        var matchingType = Types.FirstOrDefault(type =>
            Normalize(type.Name) == normalizedName ||
            type.Aliases.Any(alias =>
            {
                var normalizedAlias = Normalize(alias);
                return normalizedName.Contains(normalizedAlias, StringComparison.Ordinal);
            }));

        if (matchingType is null)
        {
            canonicalName = string.Empty;
            return false;
        }

        canonicalName = matchingType.Name;
        return true;
    }

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
