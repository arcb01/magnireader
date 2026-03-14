using System.Globalization;

namespace MagniReader.Core;

/// <summary>
/// Lightweight n-gram based language detector. Runs entirely offline.
/// Uses character trigram frequency profiles to identify the most likely language.
/// </summary>
public class LanguageDetector
{
    private readonly Dictionary<string, Dictionary<string, double>> _languageProfiles;

    public LanguageDetector()
    {
        _languageProfiles = BuildLanguageProfiles();
    }

    /// <summary>
    /// Detects the language of the given text and returns a CultureInfo.
    /// Falls back to the current UI culture if detection fails.
    /// </summary>
    public CultureInfo DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CultureInfo.CurrentUICulture;

        // Clean the text: remove numbers, punctuation, normalize whitespace
        var cleaned = new string(text.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray())
            .ToLowerInvariant();

        if (cleaned.Length < 10)
            return CultureInfo.CurrentUICulture;

        var textProfile = BuildTrigramProfile(cleaned);
        var bestLang = "en";
        var bestScore = double.MaxValue;

        foreach (var (lang, profile) in _languageProfiles)
        {
            var score = CalculateDistance(textProfile, profile);
            if (score < bestScore)
            {
                bestScore = score;
                bestLang = lang;
            }
        }

        try
        {
            return new CultureInfo(bestLang);
        }
        catch
        {
            return CultureInfo.CurrentUICulture;
        }
    }

    private static Dictionary<string, double> BuildTrigramProfile(string text)
    {
        var trigrams = new Dictionary<string, int>();
        var total = 0;

        for (int i = 0; i <= text.Length - 3; i++)
        {
            var trigram = text.Substring(i, 3);
            trigrams.TryGetValue(trigram, out int count);
            trigrams[trigram] = count + 1;
            total++;
        }

        if (total == 0) return new Dictionary<string, double>();

        // Normalize to frequencies
        return trigrams.ToDictionary(kv => kv.Key, kv => (double)kv.Value / total);
    }

    private static double CalculateDistance(Dictionary<string, double> profile1, Dictionary<string, double> profile2)
    {
        var allKeys = profile1.Keys.Union(profile2.Keys);
        double distance = 0;

        foreach (var key in allKeys)
        {
            profile1.TryGetValue(key, out double freq1);
            profile2.TryGetValue(key, out double freq2);
            distance += Math.Abs(freq1 - freq2);
        }

        return distance;
    }

    /// <summary>
    /// Builds trigram frequency profiles from representative text samples for each language.
    /// These are the "fingerprints" we compare against.
    /// </summary>
    private static Dictionary<string, Dictionary<string, double>> BuildLanguageProfiles()
    {
        var profiles = new Dictionary<string, Dictionary<string, double>>();

        // Representative text samples for common languages
        var samples = new Dictionary<string, string>
        {
            ["en"] = "the and that have with this will your from they been have which their would there could about other these some them than first also made after being where between back much because good each those people over state year into just come its like where time very when would make can more after being only these some other about into make just more first each much back also good every year could like after being other because also made where between which their would these",
            ["es"] = "que los del las por una con para está como más pero sus esta todo esta tiene hasta desde están tiene todos otros sobre puede otro estos ella entre después también cada durante algunos nosotros ellos parte donde porque puede mismo antes cual desde siendo entre tiene hasta para esta todo también puede como más pero desde",
            ["fr"] = "les des une par sur que dans est pas sont avec pour qui cette tout fait plus peut être comme mais nous ont été elle aussi leur après sans même fait encore entre avoir depuis toujours cette ceux très dans aussi peut fait tout comme plus mais entre depuis avoir encore",
            ["de"] = "die der und den das von ist des ein sich mit dem für auf nicht werden aus wird auch sind eine haben nach wie kann noch über diese werden aber durch wenn wir nur seine einen sind oder auch nach wird diese noch können",
            ["it"] = "che del della per una con sono gli dalla nella delle come più anche stato questa tutto alla essere hanno sua ogni dopo ancora prima dove perché può stesso sempre quando anche fatto tutto come prima dopo dove ancora può ogni essere",
            ["pt"] = "que dos das uma para com por mais está como são esta seu sua também pode sobre entre depois como todo desta ainda porque cada desde quando mesmo muito sido todos outros ainda desde porque também como sobre cada pode para",
            ["nl"] = "het van een die dat niet zijn met voor den als ook zijn worden meer maar deze door heeft dit nog werd door worden niet deze zijn meer maar door heeft nog werd ook voor met",
            ["pl"] = "nie jest się jak jego nas ich dla ale aby jako gdzie był być jeszcze tego został które przy wszystko może były tylko",
            ["ru"] = "что это как его для все они том так его ещё были или при что этого было после через него его что может быть",
            ["zh"] = "的了是在不了有我他这个们中来上大为和国地到以说时要就出会也年对就生能过后作里行",
            ["ja"] = "のにはをたがでとしてかもなくこそれまあいうえおきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをん",
            ["ko"] = "의에서는을로가이고하다들에게에서로부터까지와과를이가은는도만",
            ["ar"] = "من في على أن إلى عن هذا كان التي التي الذي هذه بين كانت هذا",
            ["hi"] = "के में है को से हैं कि एक यह पर और नहीं इस कर था जो भी",
            ["tr"] = "bir olan için ile veya gibi olan daha sonra kadar kendi arasında ancak çok daha sonra gibi olan",
            ["sv"] = "som det att den för och har med inte till var från har det att den för och till har det som för att och med",
            ["da"] = "der den det til har som med for ikke den det til har som med for sig efter eller",
            ["no"] = "som det den til har med for ikke var fra har det som den til har med for etter eller",
            ["fi"] = "että sen oli kun myös mutta tai voi tämä ovat tässä sekä joka olla hänen tämän"
        };

        foreach (var (lang, sample) in samples)
        {
            profiles[lang] = BuildTrigramProfile(sample.ToLowerInvariant());
        }

        return profiles;
    }
}
