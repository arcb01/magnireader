using MagniReader.Core;
using Xunit;

namespace MagniReader.Tests;

public class LanguageDetectorTests
{
    private readonly LanguageDetector _detector = new();

    private string Lang(string text, string? hint = null) => _detector.Detect(text, hint).Language;

    // ─── Ordinary text ───────────────────────────────────────────

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog while the children watch from the window.")]
    [InlineData("Please make sure that you have saved your work before you close the application.")]
    [InlineData("Accessibility matters because everyone deserves to read the web comfortably.")]
    public void English_sentences(string text)
    {
        var g = _detector.Detect(text);
        Assert.Equal("en", g.Language);
        Assert.True(g.IsConfident);
    }

    [Theory]
    [InlineData("El perro marrón salta por encima del gato mientras los niños miran desde la ventana.")]
    [InlineData("Asegúrate de que has guardado tu trabajo antes de cerrar la aplicación.")]
    [InlineData("La accesibilidad importa porque todo el mundo merece leer la web con comodidad.")]
    public void Spanish_sentences(string text)
    {
        var g = _detector.Detect(text);
        Assert.Equal("es", g.Language);
        Assert.True(g.IsConfident);
    }

    // ─── Short snippets ──────────────────────────────────────────

    [Theory]
    [InlineData("Click here to continue", "en")]
    [InlineData("Open the file", "en")]
    [InlineData("Sign in with your account", "en")]
    [InlineData("What is this?", "en")]
    [InlineData("Haz clic aquí para continuar", "es")]
    [InlineData("Abrir el archivo", "es")]
    [InlineData("¿Dónde está?", "es")]
    [InlineData("Inicia sesión con tu cuenta", "es")]
    [InlineData("Muchas gracias", "es")]
    [InlineData("Configuración avanzada", "es")]
    public void Short_snippets(string text, string expected) => Assert.Equal(expected, Lang(text));

    [Fact]
    public void Spanish_short_text_is_not_taken_for_portuguese_or_italian()
    {
        // The old trigram detector sent these to pt / it / fr
        Assert.Equal("es", Lang("Que tengas un buen día"));
        Assert.Equal("es", Lang("Como puedo cambiar la contraseña"));
        Assert.Equal("es", Lang("Una nota importante sobre el proyecto"));
    }

    [Fact]
    public void English_short_text_is_not_taken_for_dutch_or_danish()
    {
        Assert.Equal("en", Lang("Set the time and date"));
        Assert.Equal("en", Lang("Do not show this again"));
    }

    // ─── Accentless Spanish ──────────────────────────────────────

    [Theory]
    [InlineData("hola como estas? hoy hace buen tiempo y quiero ir a la playa")]
    [InlineData("la informacion del sistema esta actualizada")]
    [InlineData("tambien puedes descargar el archivo desde aqui")]
    [InlineData("manana te llamo despues de la reunion")]
    [InlineData("no se si podre ir, tengo mucho trabajo esta semana")]
    public void Spanish_without_accents(string text) => Assert.Equal("es", Lang(text));

    // ─── Names ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Yesterday I met José García and María Fernández in Barcelona.")]
    [InlineData("The report was written by Íñigo Martínez and reviewed by Begoña.")]
    [InlineData("I'm flying to São Paulo with François next week.")]
    public void English_with_spanish_or_foreign_names(string text) => Assert.Equal("en", Lang(text));

    [Theory]
    [InlineData("Ayer hablé con John Smith y Michael Johnson sobre el proyecto.")]
    [InlineData("La película de Steven Spielberg se estrena el viernes en Madrid.")]
    public void Spanish_with_english_names(string text) => Assert.Equal("es", Lang(text));

    // ─── Mixed languages ─────────────────────────────────────────

    [Theory]
    [InlineData("Haz clic en el botón Save para guardar el archivo en Google Drive.", "es")]
    [InlineData("Para hacer el deploy tienes que ejecutar el build y luego hacer push a la rama main.", "es")]
    [InlineData("We had tapas and sangría at a small bar near the Plaza Mayor.", "en")]
    [InlineData("The word 'mañana' means tomorrow, but it is often used to mean later.", "en")]
    public void Mixed_language_text_goes_to_the_main_language(string text, string expected) =>
        Assert.Equal(expected, Lang(text));

    // ─── Numbers, code, URLs ─────────────────────────────────────

    [Theory]
    [InlineData("Error 404: NullReferenceException at MagniReader.Core.SpeechEngine.Speak() line 352", "en")]
    [InlineData("El método GetBestVoiceForLanguage() devuelve null si no hay voces instaladas.", "es")]
    [InlineData("Visita https://www.example.com/docs/es/guia para más información.", "es")]
    [InlineData("Send it to soporte@empresa.es before 15/03/2025 at 10:30.", "en")]
    [InlineData("Total: 1.234,56 € (IVA incluido) — pagado el 12/05/2024", "es")]
    public void Numbers_code_and_urls_are_ignored(string text, string expected) =>
        Assert.Equal(expected, Lang(text));

    [Fact]
    public void Code_only_text_falls_back_to_the_hint()
    {
        var g = _detector.Detect("int i = 0; i < n; i++ { arr[i] = foo_bar(i); }", hint: "es");
        Assert.Equal("es", g.Language);
        Assert.False(g.IsConfident);
        Assert.Equal("en", Lang("var x = getValue(item_id); if (x > 10) return null;", hint: "en"));
        Assert.Equal("es", Lang("var x = getValue(item_id); if (x > 10) return null;", hint: "es"));
    }

    // ─── OCR noise ───────────────────────────────────────────────

    [Theory]
    [InlineData("Th3 qu1ck br0wn fox jumps over the lazy dog and runs into the forest |||", "en")]
    [InlineData("Ia informaci6n de este documento es confidencia1 y no debe ser compartida ~~", "es")]
    [InlineData("Ia inforrnacion de este docurnento es confidencial y no debe ser cornpartida", "es")]
    [InlineData("Tlie results of tlie survey are sliown in tlie table below", "en")]
    public void Ocr_noise(string text, string expected) => Assert.Equal(expected, Lang(text));

    // ─── Capitals ────────────────────────────────────────────────

    [Theory]
    [InlineData("BIENVENIDOS A LA PÁGINA DE INICIO", "es")]
    [InlineData("WELCOME TO THE HOME PAGE", "en")]
    [InlineData("SALIDA DE EMERGENCIA", "es")]
    public void All_caps(string text, string expected) => Assert.Equal(expected, Lang(text));

    [Fact]
    public void Acronyms_in_normal_text_dont_count()
    {
        Assert.Equal("es", Lang("Descarga el PDF de la NASA sobre el CERN"));
    }

    // ─── Unclear text and the hint ───────────────────────────────

    [Theory]
    [InlineData("OK")]
    [InlineData("12:45")]
    [InlineData("1.234,56")]
    [InlineData("…")]
    [InlineData("")]
    public void Unclear_text_uses_the_hint(string text)
    {
        var g = _detector.Detect(text, hint: "es");
        Assert.Equal("es", g.Language);
        Assert.False(g.IsConfident);

        Assert.Equal("en", _detector.Detect(text, hint: "en").Language);
    }

    [Fact]
    public void Unclear_text_without_hint_is_a_preferred_language()
    {
        Assert.Contains(_detector.Detect("OK").Language, new[] { "en", "es" });
        Assert.Contains(_detector.Detect(null).Language, new[] { "en", "es" });
    }

    [Fact]
    public void Hint_does_not_override_clear_text()
    {
        Assert.Equal("en", Lang("Please check your internet connection and try again.", hint: "es"));
        Assert.Equal("es", Lang("Comprueba tu conexión a internet y vuelve a intentarlo.", hint: "en"));
    }

    [Fact]
    public void Hint_breaks_ties_in_ambiguous_text()
    {
        // "no" and "me" are English and Spanish
        Assert.Equal("es", Lang("no me", hint: "es"));
        Assert.Equal("en", Lang("no me", hint: "en"));
    }

    // ─── Other languages ─────────────────────────────────────────

    [Theory]
    [InlineData("Aquest és el meu llibre i m'agrada molt perquè explica la història de la ciutat.", "ca")]
    [InlineData("Les nens juguen al parc amb els seus amics després de l'escola.", "ca")]
    [InlineData("Je pense que la nouvelle version fonctionne beaucoup mieux qu'avant, merci.", "fr")]
    [InlineData("Ich glaube, dass die neue Version viel besser funktioniert als die alte.", "de")]
    [InlineData("Penso che la nuova versione funzioni molto meglio di quella vecchia.", "it")]
    [InlineData("Acho que a nova versão funciona muito melhor do que a anterior, obrigado.", "pt")]
    [InlineData("Ik denk dat de nieuwe versie veel beter werkt dan de oude.", "nl")]
    public void Other_latin_languages_with_clear_text(string text, string expected) =>
        Assert.Equal(expected, Lang(text));

    [Theory]
    [InlineData("Привет, как дела? Сегодня хорошая погода.", "ru")]
    [InlineData("Привіт, як справи? Сьогодні гарна погода і ми їдемо.", "uk")]
    [InlineData("今日はいい天気ですね。散歩に行きましょう。", "ja")]
    [InlineData("今天天气很好，我们去公园散步吧。", "zh")]
    [InlineData("오늘 날씨가 정말 좋네요.", "ko")]
    [InlineData("Καλημέρα, τι κάνεις σήμερα;", "el")]
    [InlineData("مرحبا كيف حالك اليوم", "ar")]
    public void Other_scripts(string text, string expected) => Assert.Equal(expected, Lang(text));

    [Fact]
    public void English_with_a_few_cjk_characters_stays_english()
    {
        Assert.Equal("en", Lang("The Japanese word 猫 means cat, and it is written with one character."));
    }

    // ─── Restricted detection (voices that only speak some languages) ───

    [Fact]
    public void DetectAmong_picks_from_the_allowed_languages()
    {
        Assert.Equal("es", _detector.DetectAmong("Que tengas un buen día", "en", "es", "ca"));
        Assert.Equal("en", _detector.DetectAmong("Set the time and date", "en", "es", "ca"));
        Assert.Equal("ca", _detector.DetectAmong("Aquest és el meu llibre i m'agrada molt", "en", "es", "ca"));
    }

    [Fact]
    public void Culture_is_a_neutral_culture()
    {
        Assert.Equal("es", _detector.DetectLanguage("¿Qué hora es?").Name);
        Assert.Equal("en", _detector.DetectLanguage("What time is it?").Name);
    }
}
