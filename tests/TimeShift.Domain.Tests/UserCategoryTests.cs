using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Users;

namespace TimeShift.Domain.Tests;

/// <summary>Свои категории (spec §4.1): набор пользователя = системные + его собственные; удаление — архив.</summary>
public class UserCategoryTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => new(42, "Test", User.DefaultTimeZone, Now);

    [Fact]
    public void A_new_user_has_only_the_system_set()
    {
        var set = NewUser().Categories;

        Assert.Equal(DefaultCategories.All, set.All);
        Assert.Equal(DefaultCategories.All, set.Active);
        Assert.Same(set.Find("work"), set.FindActive("work"));
        Assert.Null(set.Find("music"));
        Assert.Null(set.Find(null));
    }

    [Fact]
    public void An_own_category_gets_a_stable_key_and_joins_the_set()
    {
        var user = NewUser();

        var music = user.AddCategory("  Музыка ", CategoryGroup.Rest, null, Now);

        Assert.StartsWith(UserCategory.KeyPrefix, music.Key);
        Assert.True(music.Key.Length <= 25, "ключ должен помещаться в данные кнопки Telegram рядом с id вопроса");
        Assert.Equal("Музыка", music.Name);
        Assert.False(CategorySet.IsSystem(music.Key));
        var found = user.Categories.FindActive(music.Key)!;
        Assert.Equal(("Музыка", CategoryGroup.Rest, null), (found.Name, found.Group, found.ParentKey));
        Assert.Equal(music.Key, user.Categories.FindByName("музыка")!.Key);
        Assert.Equal(music.Key, user.Categories.TotalsKey(music.Key));

        var other = user.AddCategory("Чтение", CategoryGroup.Useful, null, Now);
        Assert.NotEqual(music.Key, other.Key);
    }

    [Fact]
    public void A_subcategory_takes_the_group_of_its_parent_and_counts_into_it()
    {
        var user = NewUser();

        var calls = user.AddCategory("Созвоны", CategoryGroup.Lost, DefaultCategories.Work, Now);
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Useful, music.Key, Now);

        Assert.Equal(CategoryGroup.Useful, calls.Group);
        Assert.Equal(DefaultCategories.Work, user.Categories.TotalsKey(calls.Key));
        Assert.Equal(CategoryGroup.Rest, guitar.Group);
        Assert.Equal(music.Key, user.Categories.TotalsKey(guitar.Key));
    }

    [Theory]
    [InlineData("", null, "name")]
    [InlineData("   ", null, "name")]
    [InlineData("Работа", null, "name")] // занято системной
    [InlineData("работа", null, "name")]
    [InlineData("Осознанный отдых и ещё сорок символов сверху", null, "name")]
    [InlineData("Сериалы", "wasted.youtube", "parent")] // третий уровень
    [InlineData("Сериалы", "nope", "parent")]
    public void A_bad_name_or_parent_is_rejected(string name, string? parent, string field)
    {
        var user = NewUser();

        var error = Assert.Throws<CategoryException>(() => user.AddCategory(name, CategoryGroup.Rest, parent, Now));

        Assert.Equal(field, error.Field);
        Assert.Empty(user.OwnCategories);
    }

    [Fact]
    public void Names_are_unique_among_active_categories_and_the_group_must_exist()
    {
        var user = NewUser();
        user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);

        Assert.Equal("name", Assert.Throws<CategoryException>(() => user.AddCategory("музыка", CategoryGroup.Useful, null, Now)).Field);
        Assert.Equal("group", Assert.Throws<CategoryException>(() => user.AddCategory("Чтение", (CategoryGroup)99, null, Now)).Field);
        Assert.Single(user.OwnCategories);
    }

    [Fact]
    public void The_number_of_own_categories_is_limited()
    {
        var user = NewUser();
        for (var i = 0; i < User.MaxOwnCategories; i++)
        {
            user.AddCategory($"Категория {i}", CategoryGroup.Useful, null, Now);
        }

        Assert.Throws<CategoryException>(() => user.AddCategory("Ещё одна", CategoryGroup.Useful, null, Now));
    }

    [Fact]
    public void Rename_and_move_keep_the_key_and_take_the_subcategories_along()
    {
        var user = NewUser();
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Rest, music.Key, Now);
        var key = music.Key;

        user.UpdateCategory(key, "Музыка и звук", CategoryGroup.Useful, null);

        Assert.Equal(key, music.Key);
        Assert.Equal("Музыка и звук", user.Categories.Name(key));
        Assert.Equal(CategoryGroup.Useful, guitar.Group);

        // Своё имя не мешает себе же; категория с подвидами сама подвидом стать не может.
        user.UpdateCategory(key, "музыка и звук", CategoryGroup.Useful, null);
        Assert.Equal("parent", Assert.Throws<CategoryException>(() => user.UpdateCategory(key, "Музыка", CategoryGroup.Useful, DefaultCategories.Projects)).Field);
        Assert.Equal("parent", Assert.Throws<CategoryException>(() => user.UpdateCategory(key, "Музыка", CategoryGroup.Useful, key)).Field);

        user.UpdateCategory(guitar.Key, "Гитара", CategoryGroup.People, DefaultCategories.Projects);
        Assert.Equal((DefaultCategories.Projects, CategoryGroup.Useful), (guitar.ParentKey, guitar.Group));
        user.UpdateCategory(guitar.Key, "Гитара", CategoryGroup.People, null);
        Assert.Equal((null, CategoryGroup.People), (guitar.ParentKey, guitar.Group));
    }

    [Fact]
    public void System_categories_cannot_be_changed_or_removed()
    {
        var user = NewUser();

        Assert.Equal(CategoryException.SystemKey, Assert.Throws<CategoryException>(() => user.UpdateCategory("work", "Труд", CategoryGroup.Useful, null)).Field);
        Assert.Equal(CategoryException.SystemKey, Assert.Throws<CategoryException>(() => user.RemoveCategory("work", Now)).Field);
        Assert.Equal(CategoryException.UnknownKey, Assert.Throws<CategoryException>(() => user.RemoveCategory("u.nope", Now)).Field);
    }

    [Fact]
    public void Removing_archives_the_category_with_its_subcategories_and_keeps_them_known()
    {
        var user = NewUser();
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Rest, music.Key, Now);
        var reading = user.AddCategory("Чтение", CategoryGroup.Useful, null, Now);
        Assert.Null(user.TryAddBlock(new ScheduleBlock(ScheduleBlockKind.Custom, "Репетиция", guitar.Key, [DayOfWeek.Tuesday], new TimeOnly(19, 0), new TimeOnly(20, 0))));
        Assert.Null(user.TryAddBlock(new ScheduleBlock(ScheduleBlockKind.Custom, "Книга", reading.Key, [DayOfWeek.Tuesday], new TimeOnly(21, 0), new TimeOnly(22, 0))));

        var removed = user.RemoveCategory(music.Key, Now.AddDays(1));

        Assert.Equal([music.Key, guitar.Key], removed);
        var set = user.Categories;
        Assert.Null(set.FindActive(music.Key));
        Assert.Null(set.FindActive(guitar.Key));
        Assert.Null(set.FindByName("Музыка"));
        Assert.True(set.IsArchived(guitar.Key));
        Assert.DoesNotContain(set.Active, c => c.Key == music.Key || c.Key == guitar.Key);

        // Старые отрезки: имя и родитель по-прежнему известны.
        Assert.Equal("Гитара", set.Name(guitar.Key));
        Assert.Equal(music.Key, set.TotalsKey(guitar.Key));
        Assert.Equal("Музыка", set.Name(music.Key));

        // Блок с удалённой категорией остался, но без категории; чужой блок не тронут.
        Assert.Null(user.Blocks.Single(b => b.Title == "Репетиция").CategoryKey);
        Assert.Equal(reading.Key, user.Blocks.Single(b => b.Title == "Книга").CategoryKey);

        Assert.Throws<CategoryException>(() => user.RemoveCategory(music.Key, Now));
        Assert.Throws<CategoryException>(() => user.UpdateCategory(music.Key, "Музыка", CategoryGroup.Rest, null));
        Assert.Equal("parent", Assert.Throws<CategoryException>(() => user.AddCategory("Пианино", CategoryGroup.Rest, music.Key, Now)).Field);
    }

    [Fact]
    public void Adding_a_removed_name_again_brings_the_same_category_back()
    {
        var user = NewUser();
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);
        user.RemoveCategory(music.Key, Now);

        var again = user.AddCategory("музыка", CategoryGroup.Useful, null, Now.AddDays(2));

        Assert.Same(music, again);
        Assert.False(again.IsArchived);
        Assert.Equal(("музыка", CategoryGroup.Useful), (again.Name, again.Group));
        Assert.Single(user.OwnCategories);
        Assert.NotNull(user.Categories.FindActive(music.Key));
    }

    [Fact]
    public void A_removed_category_with_subcategories_does_not_come_back_as_a_subcategory()
    {
        var user = NewUser();
        var music = user.AddCategory("Музыка", CategoryGroup.Rest, null, Now);
        var guitar = user.AddCategory("Гитара", CategoryGroup.Rest, music.Key, Now);
        user.RemoveCategory(music.Key, Now);

        // Иначе «Гитара» оказалась бы на третьем уровне: заводится новая категория, старая остаётся в архиве.
        var again = user.AddCategory("Музыка", CategoryGroup.Rest, DefaultCategories.MindfulRest, Now);

        Assert.NotSame(music, again);
        Assert.True(music.IsArchived);
        Assert.Equal(music.Key, user.Categories.TotalsKey(guitar.Key));
        Assert.Equal(DefaultCategories.MindfulRest, user.Categories.TotalsKey(again.Key));
    }
}
