using TimeShift.Domain.Categories;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Conversations;

/// <summary>
/// <c>/categories</c> (spec §4.1): набор категорий и свои категории — добавить, переименовать, удалить. Правила
/// набора живут в <see cref="User"/>; здесь только диалог. Кнопки — <c>cat:…</c>.
/// </summary>
public sealed partial class ConversationService
{
    private const string CategoryButton = "cat:";

    /// <summary>Пометка своей категории в списке.</summary>
    private const string OwnMark = "🔹";

    private static BotReply CategoriesMenu(User user, bool replaces = false, string? note = null)
    {
        var set = user.Categories;
        var lines = new List<string>();
        if (note is not null)
        {
            lines.Add(note);
            lines.Add(string.Empty);
        }

        lines.Add("Категории:");
        lines.Add(string.Empty);
        foreach (var group in Enum.GetValues<CategoryGroup>())
        {
            var top = set.Active.Where(c => c.Group == group && c.ParentKey is null).Select(c =>
            {
                var children = set.Active.Where(x => x.ParentKey == c.Key).Select(Label).ToList();
                return children.Count == 0 ? Label(c) : $"{Label(c)} ({string.Join(", ", children)})";
            });
            lines.Add($"{DefaultCategories.GroupName(group)}: {string.Join(" · ", top)}");
        }

        var own = user.OwnCategories.Where(c => !c.IsArchived).OrderBy(c => c.CreatedAtUtc).ToList();
        lines.Add(string.Empty);
        lines.Add(own.Count == 0
            ? "Своих категорий пока нет. Добавь свою — она появится в выборе и в итогах дня и недели."
            : $"{OwnMark} — твои. Нажми на свою категорию, чтобы переименовать или удалить её.");

        var rows = own
            .Select(c => new ReplyButton($"{OwnMark} {c.Name}", $"{CategoryButton}open:{c.Key}"))
            .Chunk(2)
            .Select(c => (IReadOnlyList<ReplyButton>)c)
            .ToList();
        rows.Add([new ReplyButton("➕ Добавить категорию", $"{CategoryButton}add")]);
        return new BotReply(string.Join('\n', lines), rows, replaces);

        static string Label(Category c) => CategorySet.IsSystem(c.Key) ? c.Name : $"{OwnMark}{c.Name}";
    }

    private async Task<IReadOnlyList<BotReply>> OnCategoryButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        var parts = data.Split(':', 3);
        var (action, argument) = (parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 ? parts[2] : string.Empty);
        user.DialogState = null;
        switch (action)
        {
            case "menu":
                return [CategoriesMenu(user, replaces: true)];

            case "add":
                var groups = Enum.GetValues<CategoryGroup>()
                    .Select(g => new ReplyButton(DefaultCategories.GroupName(g), $"{CategoryButton}grp:{g}"))
                    .Chunk(2)
                    .Select(c => (IReadOnlyList<ReplyButton>)c)
                    .ToList();
                groups.Add([new ReplyButton("↩️ Назад", $"{CategoryButton}menu")]);
                return [new BotReply("В какую группу добавить категорию?", groups, ReplacesSource: true)];

            case "grp" when Enum.TryParse<CategoryGroup>(argument, out var group) && Enum.IsDefined(group):
                user.DialogState = $"E:{Step.CategoryName}:{group}";
                return
                [
                    new BotReply(
                        $"Напиши название новой категории в группе «{DefaultCategories.GroupName(group)}».\n\n" +
                        "Подвид существующей категории — через двоеточие: «Работа: Созвоны».",
                        [],
                        ReplacesSource: true),
                ];
        }

        if (user.OwnCategories.FirstOrDefault(c => c.Key == argument && !c.IsArchived) is not { } category)
        {
            return [CategoriesMenu(user, replaces: true, note: "Этой категории уже нет.")];
        }

        switch (action)
        {
            case "open":
                var where = category.ParentKey is { } parent
                    ? $"подвид категории «{user.Categories.Name(parent)}»"
                    : $"группа «{DefaultCategories.GroupName(category.Group)}»";
                return
                [
                    new BotReply(
                        $"{OwnMark} «{category.Name}» — {where}.\n\n" +
                        "Если удалить, прошлые записи останутся как были, а в выборе её больше не будет.",
                        [
                            [
                                new ReplyButton("✏️ Переименовать", $"{CategoryButton}ren:{category.Key}"),
                                new ReplyButton("🗑 Удалить", $"{CategoryButton}del:{category.Key}"),
                            ],
                            [new ReplyButton("↩️ Назад", $"{CategoryButton}menu")],
                        ],
                        ReplacesSource: true),
                ];

            case "ren":
                user.DialogState = $"E:{Step.CategoryRename}:{category.Key}";
                return [new BotReply($"Напиши новое название для «{category.Name}».", [], ReplacesSource: true)];

            case "del":
                var name = category.Name;
                var forgotten = await tracking.RemoveCategoryAsync(user, category.Key, now, ct);
                var note = $"Удалил «{name}». Прошлые записи с ней остались как были."
                    + (forgotten > 0 ? $" Занятий без категории теперь: {forgotten} — спрошу о них заново." : string.Empty);
                return [CategoriesMenu(user, replaces: true, note: note)];

            default:
                return [CategoriesMenu(user, replaces: true)];
        }
    }

    /// <summary>Название новой категории: «Музыка» — в выбранную группу, «Работа: Созвоны» — подвид.</summary>
    private IReadOnlyList<BotReply> AddCategoryByText(User user, string text, string? groupName, DateTime now)
    {
        if (!Enum.TryParse<CategoryGroup>(groupName, out var group))
        {
            user.DialogState = null;
            return [CategoriesMenu(user)];
        }

        var (name, parentKey) = (text, (string?)null);
        if (text.IndexOf(':') is var colon and > 0 && user.Categories.FindByName(text[..colon]) is { ParentKey: null } parent)
        {
            (name, parentKey) = (text[(colon + 1)..], parent.Key);
        }

        try
        {
            var added = user.AddCategory(name, group, parentKey, now);
            user.DialogState = null;
            var where = added.ParentKey is { } p ? $"подвид «{user.Categories.Name(p)}»" : DefaultCategories.GroupName(added.Group);
            return [CategoriesMenu(user, note: $"Добавил: «{added.Name}» ({where}).")];
        }
        catch (CategoryException e)
        {
            // Ждём название дальше: ошибка обычно в нём (занято, слишком длинное).
            return [BotReply.Plain($"{e.Message} Напиши другое название или /categories, чтобы выйти.")];
        }
    }

    private IReadOnlyList<BotReply> RenameCategoryByText(User user, string text, string? key)
    {
        if (user.OwnCategories.FirstOrDefault(c => c.Key == key && !c.IsArchived) is not { } category)
        {
            user.DialogState = null;
            return [CategoriesMenu(user, note: "Этой категории уже нет.")];
        }

        try
        {
            var old = category.Name;
            user.UpdateCategory(category.Key, text, category.Group, category.ParentKey);
            user.DialogState = null;
            return [CategoriesMenu(user, note: $"Переименовал: «{old}» → «{category.Name}».")];
        }
        catch (CategoryException e)
        {
            return [BotReply.Plain($"{e.Message} Напиши другое название или /categories, чтобы выйти.")];
        }
    }
}
