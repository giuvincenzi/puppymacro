using System.Globalization;
using System.Linq;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class LanguageTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Fact]
    public void The_menu_lists_every_language_alphabetically_by_its_own_name()
    {
        string[] names = AppLanguages.ForMenu().Select(l => l.NativeName).ToArray();

        Assert.Equal(13, names.Length);
        Assert.Equal(new[] { "Deutsch", "English", "Español", "Français", "Italiano", "Português (Brasil)", "Română", "Shqip" },
            names.Take(8));
        Assert.Equal(names.OrderBy(n => n, System.StringComparer.InvariantCulture), names);
    }

    [Theory]
    [InlineData(null, "it-IT", "it")]
    [InlineData(null, "it-CH", "it")]
    [InlineData(null, "de-AT", "de")]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData(null, "zh-CN", "zh-Hans")]
    [InlineData(null, "zh-TW", "en")]
    [InlineData(null, "ar-EG", "ar")]
    [InlineData(null, "sq-AL", "sq")]
    [InlineData(null, "ro-MD", "ro")]
    [InlineData(null, "nl-NL", "en")]
    [InlineData("fr", "it-IT", "fr")]
    [InlineData("xx", "it-IT", "it")]
    public void The_texts_follow_the_setting_or_else_windows_or_else_english(string? setting, string windows, string expected) =>
        Assert.Equal(expected, AppLanguages.Resolve(setting, C(windows)));

    [Fact]
    public void Numbers_and_dates_keep_windows_format_while_it_is_in_the_same_language()
    {
        Assert.Equal("it-CH", AppLanguages.FormatCulture("it", C("it-CH")).Name);
        Assert.Equal("zh-CN", AppLanguages.FormatCulture("zh-Hans", C("zh-CN")).Name);
        // Another language: its own usual format, so a date never mixes two languages.
        Assert.Equal("it-IT", AppLanguages.FormatCulture("it", C("en-US")).Name);
        Assert.Equal("de-DE", AppLanguages.FormatCulture("de", C("en-GB")).Name);
    }

    [Fact]
    public void Arabic_chosen_by_the_user_keeps_gregorian_dates()
    {
        CultureInfo format = AppLanguages.FormatCulture("ar", C("en-US"));

        Assert.StartsWith("ar", format.Name);
        Assert.IsType<GregorianCalendar>(format.DateTimeFormat.Calendar);
    }

    [Theory]
    [InlineData("en", 0, "other")]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    [InlineData("de", 1, "one")]
    [InlineData("sq", 1, "one")]
    [InlineData("it", 0, "other")]
    [InlineData("it", 1_000_000, "many")]
    [InlineData("es", 1, "one")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 1, "one")]
    [InlineData("fr", 2, "other")]
    [InlineData("pt-BR", 0, "one")]
    [InlineData("pt-BR", 1, "one")]
    [InlineData("ro", 1, "one")]
    [InlineData("ro", 0, "few")]
    [InlineData("ro", 19, "few")]
    [InlineData("ro", 20, "other")]
    [InlineData("ro", 101, "few")]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 11, "many")]
    [InlineData("ru", 2, "few")]
    [InlineData("ru", 24, "few")]
    [InlineData("ru", 12, "many")]
    [InlineData("ru", 5, "many")]
    [InlineData("ru", 0, "many")]
    [InlineData("ar", 0, "zero")]
    [InlineData("ar", 1, "one")]
    [InlineData("ar", 2, "two")]
    [InlineData("ar", 3, "few")]
    [InlineData("ar", 110, "few")]
    [InlineData("ar", 11, "many")]
    [InlineData("ar", 99, "many")]
    [InlineData("ar", 100, "other")]
    [InlineData("ja", 1, "other")]
    [InlineData("ko", 1, "other")]
    [InlineData("zh-Hans", 1, "other")]
    public void Plural_forms_follow_the_cldr_rules(string language, long count, string expected) =>
        Assert.Equal(expected, Plurals.Category(language, count));

    [Fact]
    public void The_texts_are_read_from_the_english_resources()
    {
        Assert.Equal("Language", Strings.ResourceManager.GetString(nameof(Strings.Settings_Language), CultureInfo.InvariantCulture));
    }
}
