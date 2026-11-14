using TimeShift.Domain.Categories;

namespace TimeShift.Domain.Tests;

/// <summary>Правила по умолчанию spec §4.2.</summary>
public class ClassifierTests
{
    [Theory]
    [InlineData("Сериал", DefaultCategories.MindfulRest)]
    [InlineData("Смотрю фильм", DefaultCategories.MindfulRest)]
    [InlineData("Прогулка", DefaultCategories.MindfulRest)]
    [InlineData("TikTok", DefaultCategories.WastedShortVideo)]
    [InlineData("Листаю Reels", DefaultCategories.WastedShortVideo)]
    [InlineData("YouTube Shorts", DefaultCategories.WastedShortVideo)]
    [InlineData("Dota 2", DefaultCategories.WastedMultiplayer)]
    [InlineData("Играю в доту", DefaultCategories.WastedMultiplayer)]
    [InlineData("CS", DefaultCategories.WastedMultiplayer)]
    [InlineData("Работа", DefaultCategories.Work)]
    [InlineData("Созвон", DefaultCategories.Work)]
    [InlineData("Английский", DefaultCategories.Study)]
    [InlineData("Пробежка", DefaultCategories.Sport)]
    [InlineData("Готовлю ужин", DefaultCategories.Household)]
    [InlineData("Метро", DefaultCategories.Commute)]
    [InlineData("У родителей", DefaultCategories.Family)]
    [InlineData("С друзьями", DefaultCategories.Friends)]
    [InlineData("YouTube", null)]
    [InlineData("Играю в Elden Ring", null)]
    [InlineData("Гитара", null)]
    public void Default_rules(string name, string? category)
    {
        Assert.Equal(category, Classifier.Guess(name));
    }

    [Fact]
    public void YouTube_and_games_are_recognized_for_questions()
    {
        Assert.True(Classifier.IsYouTube("Смотрю ютуб"));
        Assert.True(Classifier.IsGame("Играю в Elden Ring"));
        Assert.False(Classifier.IsGame("Работа"));
    }
}
