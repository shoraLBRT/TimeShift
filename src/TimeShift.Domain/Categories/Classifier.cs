namespace TimeShift.Domain.Categories;

/// <summary>
/// Правила классификации по умолчанию (spec §4.2) по названию занятия. Возвращает категорию, если она очевидна;
/// <c>null</c> — спросить пользователя (YouTube, незнакомые игры, всё неочевидное).
/// </summary>
public static class Classifier
{
    private static readonly string[] Multiplayer =
        ["dota", "дота", "доту", "доте", "cs", "кс ", "контр", "counter", "valorant", "валорант", "pubg", "пабг", "fortnite",
         "фортнайт", "league of legends", "лол ", "apex", "overwatch", "warzone", "tarkov", "тарков", "rust", "раст"];

    private static readonly string[] ShortVideo =
        ["shorts", "шортс", "reels", "рилс", "tiktok", "тикток", "тик-ток", "инстаграм", "instagram", "инста", "лента",
         "соцсет", "вк ", "вконтакте", "телеграм-канал", "телефон", "листал", "листаю", "скролл", "скрол"];

    private static readonly string[] YouTube = ["youtube", "ютуб", "ютюб", "youtu"];

    private static readonly string[] Games = ["игр", "game", "гейм", "катк", "плейстейшн", "playstation", "xbox", "steam", "стим"];

    public static string? Guess(string name)
    {
        var t = $" {name.Trim().ToLowerInvariant().Replace('ё', 'е')} ";
        return t switch
        {
            _ when Has(t, Multiplayer) => DefaultCategories.WastedMultiplayer,
            _ when Has(t, ShortVideo) => DefaultCategories.WastedShortVideo,
            _ when IsYouTube(name) => null,
            _ when t.Contains("фильм") || t.Contains("кино") || t.Contains("сериал") => DefaultCategories.MindfulRest,
            _ when t.Contains("прогул") || t.Contains("гулял") || t.Contains("гуляю") || t.Contains("медитац") => DefaultCategories.MindfulRest,
            _ when t.Contains("работ") || t.Contains("смена") || t.Contains("офис") || t.Contains("совещан") || t.Contains("созвон") => DefaultCategories.Work,
            _ when t.Contains("учеб") || t.Contains("пары") || t.Contains("универ") || t.Contains("школ") || t.Contains("курс")
                   || t.Contains("лекци") || t.Contains("английск") || t.Contains("домашк") => DefaultCategories.Study,
            _ when t.Contains("трениров") || t.Contains("спорт") || t.Contains(" зал") || t.Contains("бег") || t.Contains("пробежк")
                   || t.Contains("йог") || t.Contains("бассейн") || t.Contains("футбол") => DefaultCategories.Sport,
            _ when t.Contains("дорог") || t.Contains("езд") || t.Contains("метро") || t.Contains("такси") => DefaultCategories.Commute,
            _ when t.Contains("проект") || t.Contains("пет-проект") => DefaultCategories.Projects,
            _ when t.Contains("готов") || t.Contains("уборк") || t.Contains("убира") || t.Contains("магазин") || t.Contains("покупк")
                   || t.Contains("душ") || t.Contains("обед") || t.Contains("ужин") || t.Contains("завтрак") || t.Contains(" еда") => DefaultCategories.Household,
            _ when t.Contains("родител") || t.Contains("семь") || t.Contains("мама") || t.Contains("папа") => DefaultCategories.Family,
            _ when t.Contains("друз") || t.Contains("друг") => DefaultCategories.Friends,
            _ when t.Contains(" сон") || t.Contains("спал") || t.Contains("сплю") => DefaultCategories.Sleep,
            _ => null,
        };
    }

    /// <summary>YouTube: спросить — отдых, учёба или залипание (spec §4.2).</summary>
    public static bool IsYouTube(string name) => Has(name.ToLowerInvariant(), YouTube);

    /// <summary>Игра: спросить про конкретную игру один раз — синглплеер (отдых) или мультиплеер (залипание).</summary>
    public static bool IsGame(string name) => Has($" {name.ToLowerInvariant()} ", Games);

    private static bool Has(string text, string[] needles) => needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
