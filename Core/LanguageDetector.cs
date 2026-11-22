using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MagniReader.Core;

/// <summary>
/// The language a text was detected as, with how sure the detector is.
/// </summary>
/// <param name="Language">Two-letter code, e.g. "en", "es".</param>
/// <param name="Confidence">0..1: how far ahead the winner is of the runner-up.</param>
/// <param name="IsConfident">True when the text held enough evidence; false when the
/// result is the fallback (hint or preferred language) or a narrow win.</param>
public readonly record struct LanguageGuess(string Language, double Confidence, bool IsConfident)
{
    public CultureInfo Culture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(Language); }
            catch { return CultureInfo.GetCultureInfo("en"); }
        }
    }
}

/// <summary>
/// Offline language detector tuned for reading screen text, where snippets are often
/// short, mix languages, and contain names, numbers, code or OCR noise.
///
/// How it decides:
///   1. Non-Latin scripts (Cyrillic, Greek, Arabic, Hebrew, Devanagari, Thai, CJK) are
///      settled by the script itself.
///   2. Latin text is scored per language from: common short words (the strongest signal,
///      matched without accents, so "como esta" counts as Spanish), letters that only some
///      languages use (ñ ¿ ¡ ç ã ß l·l …), and a per-word letter-trigram vote.
///   3. Tokens that are not words of the running text are ignored: numbers, anything with
///      digits (OCR noise like "informaci6n"), URLs, e-mails, file names, code identifiers
///      (camelCase, snake_case, Foo.Bar()), and the accents of capitalized names in the
///      middle of a sentence (an English sentence about "José García" stays English).
///   4. The preferred languages (English and Spanish by default) and the hint (the
///      language of the reading so far) get a small head start, and another language only
///      wins with a clear margin. With too little evidence (a button label, "OK", a
///      number), the result is the hint, else the preferred language, marked not confident.
/// </summary>
public class LanguageDetector
{
    /// <summary>Latin-script languages the detector can tell apart.</summary>
    public static readonly string[] LatinLanguages = { "en", "es", "ca", "fr", "pt", "it", "de", "nl" };

    /// <summary>
    /// Choices of the reading-language picker. "auto" = detect; the rest force that language.
    /// Names are in their own language, as people look for them.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string Name)> ManualChoices = new[]
    {
        ("auto", "Auto-detect"),
        ("en", "English"),
        ("es", "Español"),
        ("ca", "Català"),
        ("fr", "Français"),
        ("pt", "Português"),
        ("it", "Italiano"),
        ("de", "Deutsch"),
        ("nl", "Nederlands"),
    };

    /// <summary>
    /// Languages the user mostly reads. They win ties and short or unclear text; another
    /// language needs a clear margin. The first one is the last-resort fallback.
    /// </summary>
    public IReadOnlyList<string> PreferredLanguages { get; set; } = new[] { "en", "es" };

    // Weights (tuned with the unit tests in tests/MagniReader.Tests)
    private const double PreferredPrior = 0.25;
    private const double HintPrior = 0.35;
    private const double TrigramVote = 0.4;
    private const double TrigramMargin = 0.35;   // avg log-prob per trigram the winner must lead by
    private const double MinEvidence = 0.75;     // below this, use the fallback
    private const double ForeignMargin = 0.75;   // a non-preferred language must lead the best preferred one by this

    // ─── Public API ──────────────────────────────────────────────

    /// <summary>
    /// Detects the language of <paramref name="text"/>. Never throws. Short or unclear text
    /// returns the preferred language (English by default).
    /// </summary>
    public CultureInfo DetectLanguage(string text) => Detect(text).Culture;

    /// <summary>Like <see cref="DetectLanguage(string)"/>, falling back to <paramref name="hint"/> when unsure.</summary>
    public CultureInfo DetectLanguage(string text, string? hint) => Detect(text, hint).Culture;

    /// <summary>
    /// Picks the most likely language among <paramref name="allowed"/> (e.g. en, es, ca).
    /// Used when the full detection picked a language the selected voices can't speak.
    /// </summary>
    public string DetectAmong(string text, params string[] allowed) =>
        Detect(text, null, allowed.Length > 0 ? allowed : null).Language;

    /// <summary>
    /// Detects the language of <paramref name="text"/>, with a confidence.
    /// </summary>
    /// <param name="hint">Language assumed when the text is unclear (e.g. the language of
    /// the paragraphs read so far). It also gets a small head start.</param>
    /// <param name="allowed">Restricts the result to these languages (null = all).</param>
    public LanguageGuess Detect(string? text, string? hint = null, IReadOnlyCollection<string>? allowed = null)
    {
        try
        {
            return DetectCore(text ?? "", Normalize(hint), allowed);
        }
        catch
        {
            return new LanguageGuess(Fallback(Normalize(hint), allowed), 0, false);
        }
    }

    // ─── Detection ───────────────────────────────────────────────

    private LanguageGuess DetectCore(string text, string? hint, IReadOnlyCollection<string>? allowed)
    {
        bool Allowed(string lang) => allowed == null || allowed.Contains(lang);

        if (string.IsNullOrWhiteSpace(text))
            return new LanguageGuess(Fallback(hint, allowed), 0, false);

        text = text.Normalize(NormalizationForm.FormC);

        // 1. Scripts other than Latin decide by themselves
        var script = DetectScript(text);
        if (script != null && Allowed(script))
            return new LanguageGuess(script, 0.95, true);

        // 2. Latin text
        var candidates = (allowed ?? (IReadOnlyCollection<string>)LatinLanguages)
            .Where(l => LatinLanguages.Contains(l)).Distinct().ToList();
        if (candidates.Count == 0)
            return new LanguageGuess(Fallback(hint, allowed), 0, false);
        if (candidates.Count == 1)
            return new LanguageGuess(candidates[0], 1, true);

        var tokens = Tokenize(text);
        var evidence = candidates.ToDictionary(l => l, _ => 0.0);

        // Inverted punctuation is Spanish only, wherever it appears
        if (evidence.ContainsKey("es"))
        {
            int inverted = text.Count(c => c is '¿' or '¡');
            evidence["es"] += Math.Min(inverted, 2) * 1.5;
        }

        foreach (var tok in tokens)
        {
            // Common short words. Shared words split their weight between the languages
            // that use them. Capitalized mid-sentence words (names, titles) count half.
            double weight = tok.IsName ? 0.5 : 1.0;
            if (tok.Lower == "i")
            {
                // "I" is English; lowercase "i" is Catalan ("and"), or a loop variable
                string l = tok.IsPronounI ? "en" : "ca";
                if (evidence.ContainsKey(l)) evidence[l] += 1.0;
                continue;
            }
            foreach (var form in tok.LookupForms)
            {
                var langs = candidates.Where(l => StopWords[l].Contains(form)).ToList();
                if (langs.Count == 0) continue;
                foreach (var l in langs) evidence[l] += weight / langs.Count;
                break; // first matching form only (whole word before its parts)
            }

            if (tok.IsName) continue; // a name's letters say nothing about the sentence

            AddLetterEvidence(tok.Lower, evidence);
            AddEndingEvidence(tok.Folded, evidence);

            // Letter-trigram vote for words that aren't common short words
            if (tok.Folded.Length >= 3 && !tok.IsStopWord)
            {
                var vote = TrigramWinner(tok.Folded, candidates);
                if (vote != null) evidence[vote] += TrigramVote;
            }
        }

        // Source code: the few words between the symbols are identifiers, not language
        int codeSymbols = text.Count(c => c is '{' or '}' or ';' or '=' or '<' or '>' or '[' or ']' or '(' or ')');
        if (codeSymbols >= 4 && codeSymbols * 2 >= tokens.Count)
            foreach (var l in candidates) evidence[l] *= 0.3;

        // 3. Priors, decision
        var scores = new Dictionary<string, double>(evidence);
        foreach (var l in PreferredLanguages)
            if (scores.ContainsKey(l)) scores[l] += PreferredPrior;
        if (hint != null && scores.ContainsKey(hint)) scores[hint] += HintPrior;

        var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
        var best = ranked[0];

        if (evidence.Values.Max() < MinEvidence)
            return new LanguageGuess(Fallback(hint, allowed), 0, false);

        // Another language needs a clear lead over the best preferred one
        string winner = best.Key;
        if (!PreferredLanguages.Contains(winner))
        {
            var bestPreferred = ranked.FirstOrDefault(kv => PreferredLanguages.Contains(kv.Key));
            if (bestPreferred.Key != null && best.Value - bestPreferred.Value < ForeignMargin)
                winner = bestPreferred.Key;
        }

        double winnerScore = scores[winner];
        double runnerUp = ranked.Where(kv => kv.Key != winner).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        double margin = winnerScore - runnerUp;
        double confidence = Math.Clamp(margin / (winnerScore + 0.5), 0, 1);
        bool confident = evidence[winner] >= 1.5 && margin >= 0.75;
        return new LanguageGuess(winner, confidence, confident);
    }

    private string Fallback(string? hint, IReadOnlyCollection<string>? allowed)
    {
        bool Allowed(string lang) => allowed == null || allowed.Contains(lang);
        if (hint != null && Allowed(hint)) return hint;

        // The Windows display language, when it's one the user reads
        string ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (PreferredLanguages.Contains(ui) && Allowed(ui)) return ui;

        foreach (var l in PreferredLanguages)
            if (Allowed(l)) return l;
        return allowed?.FirstOrDefault() ?? "en";
    }

    private static string? Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        lang = lang.Trim().ToLowerInvariant();
        if (lang == "auto") return null;
        int dash = lang.IndexOf('-');
        return dash > 0 ? lang[..dash] : lang;
    }

    // ─── Scripts ─────────────────────────────────────────────────

    /// <summary>The language of a non-Latin script that makes up most of the letters, else null.</summary>
    private static string? DetectScript(string text)
    {
        int latin = 0, cyrillic = 0, greek = 0, arabic = 0, hebrew = 0, devanagari = 0,
            thai = 0, hangul = 0, kana = 0, han = 0, ukrainian = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            switch (c)
            {
                case <= 'ɏ': latin++; break;
                case >= 'Ḁ' and <= 'ỿ': latin++; break;
                case >= 'Ͱ' and <= 'Ͽ': greek++; break;
                case >= 'Ѐ' and <= 'ӿ':
                    cyrillic++;
                    if (c is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ') ukrainian++;
                    break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                case >= 'ऀ' and <= 'ॿ': devanagari++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= 'ᄀ' and <= 'ᇿ':
                case >= '㄰' and <= '㆏':
                case >= '가' and <= '힯': hangul++; break;
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '㐀' and <= '䶿':
                case >= '一' and <= '鿿': han++; break;
            }
        }
        int other = cyrillic + greek + arabic + hebrew + devanagari + thai + hangul + kana + han;
        if (other == 0 || other * 2 < latin) return null;

        // CJK characters carry a whole word each, so weigh them up against Latin letters
        var counts = new (string lang, int n)[]
        {
            (ukrainian > 0 ? "uk" : "ru", cyrillic), ("el", greek), ("ar", arabic), ("he", hebrew),
            ("hi", devanagari), ("th", thai), ("ko", hangul),
            (kana > 0 ? "ja" : "zh", kana + han),
        };
        var top = counts.OrderByDescending(x => x.n).First();
        return top.n > 0 ? top.lang : null;
    }

    // ─── Tokens ──────────────────────────────────────────────────

    private sealed class Token
    {
        public string Lower = "";            // lowercase, with accents
        public string Folded = "";           // lowercase, no accents
        public bool IsName;                  // capitalized in the middle of a sentence
        public bool IsStopWord;
        public bool IsPronounI;              // English "I", "I'm"
        public List<string> LookupForms = new();
    }

    private static readonly Regex UrlOrEmail = new(
        @"(?:https?://|www\.)\S+|\S+@\S+\.\S+|\b[a-z]:\\\S*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly char[] SentenceEnd = { '.', '!', '?', ':', ';', '¿', '¡', '\n', '•', '·' };

    private static List<Token> Tokenize(string text)
    {
        text = UrlOrEmail.Replace(text, " ");
        text = text.Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'').Replace('´', '\'');

        // ALL-CAPS text (headings, OCR of signs) is read as normal words; in ordinary text
        // all-caps words are acronyms (PDF, NASA) and are skipped.
        int upper = text.Count(char.IsUpper), lower = text.Count(char.IsLower);
        bool mostlyCaps = upper > lower;

        var tokens = new List<Token>();
        bool sentenceStart = true;
        foreach (Match m in Regex.Matches(text, @"\S+"))
        {
            string raw = m.Value;
            bool startsSentence = sentenceStart || raw[0] is '¿' or '¡' or '"' or '«' or '“' or '(' or '-' or '—';
            char last = raw[^1];
            sentenceStart = SentenceEnd.Contains(last) || (raw.Length > 1 && last is '"' or '»' or '”' or ')' && SentenceEnd.Contains(raw[^2]));

            string word = raw.Trim(TrimChars);
            if (word.Length == 0) continue;
            if (!IsWordLike(word, mostlyCaps)) continue;

            bool capitalized = char.IsUpper(word[0]) && !mostlyCaps;
            if (word.Length > 1 && word.Skip(1).Where(char.IsLetter).All(char.IsUpper) && !mostlyCaps)
                continue; // acronym

            // Hyphenated words: score each part ("bien-estar", "well-known")
            foreach (var part in word.Split('-', '‐', '–'))
            {
                if (part.Length == 0) continue;
                string lowerPart = part.ToLowerInvariant();
                var tok = new Token
                {
                    Lower = lowerPart,
                    Folded = Fold(lowerPart),
                    IsName = capitalized && !startsSentence && word != "I" && !word.StartsWith("I'"),
                };
                tok.LookupForms.Add(tok.Folded);
                int apos = tok.Folded.IndexOf('\'');
                if (apos > 0)
                {
                    // "l'home", "d'aquest", "c'est", "qu'il": the elided article/pronoun
                    tok.LookupForms.Add(tok.Folded[..(apos + 1)]);
                    if (apos < tok.Folded.Length - 1) tok.LookupForms.Add(tok.Folded[(apos + 1)..]);
                    tok.Folded = tok.Folded.Replace("'", "");
                }
                tok.IsPronounI = part == "I" || part.StartsWith("I'");
                tok.IsStopWord = tok.LookupForms.Any(f => AllStopWords.Contains(f));
                tokens.Add(tok);
            }
        }
        return tokens;
    }

    private static readonly char[] TrimChars =
        ".,;:!?¡¿\"'()[]{}<>«»“”„‘*_#|~^=+/\\…•·–—-%&@$€£".ToCharArray();

    /// <summary>
    /// False for things that aren't words of the text: numbers, OCR noise with digits,
    /// code identifiers (camelCase, snake_case, Foo.Bar, a=b), very long strings.
    /// </summary>
    private static bool IsWordLike(string w, bool mostlyCaps)
    {
        if (w.Length > 30) return false;
        int letters = 0;
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            if (char.IsDigit(c)) return false;
            if (c is '_' or '.' or '/' or '\\' or '=' or '<' or '>' or '(' or ')' or '{' or '}' or '[' or ']'
                or '@' or '#' or '$' or '|' or '&' or '+' or '*' or ':' or ';' or ',')
                return false;
            if (char.IsLetter(c))
            {
                letters++;
                // camelCase / PascalCase inside a word (but not in ALL-CAPS text, nor
                // Irish/Scottish names like McDonald, which are rare enough to ignore)
                if (!mostlyCaps && i > 0 && char.IsUpper(c) && char.IsLower(w[i - 1]))
                    return false;
            }
            else if (c is not ('\'' or '-' or '‐' or '–' or '·' or 'ŀ'))
            {
                return false; // symbols, emoji, box-drawing: OCR noise
            }
        }
        return letters > 0 && letters * 2 >= w.Length;
    }

    /// <summary>Lowercase without accents (ñ → n, ç → c, l·l → ll).</summary>
    internal static string Fold(string s)
    {
        s = s.Replace("·", "").Replace("ŀ", "l");
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'ß' => 's', 'œ' => 'o', 'æ' => 'a', 'ø' => 'o', _ => c });
        }
        return sb.ToString().ToLowerInvariant();
    }

    // ─── Letters and endings ─────────────────────────────────────

    /// <summary>Letters that only some of the languages use.</summary>
    private static readonly (string letters, double weight, string[] langs)[] LetterFeatures =
    {
        ("ñ", 1.5, new[] { "es" }),
        ("ã", 1.5, new[] { "pt" }),
        ("õ", 1.5, new[] { "pt" }),
        ("ß", 1.5, new[] { "de" }),
        ("ä", 1.2, new[] { "de" }),
        ("ö", 1.2, new[] { "de" }),
        ("ü", 0.8, new[] { "de" }),          // also Spanish pingüino, but rare there
        ("œ", 1.5, new[] { "fr" }),
        ("ç", 0.8, new[] { "ca", "fr", "pt" }),
        ("à", 0.6, new[] { "ca", "fr", "it", "pt" }),
        ("è", 0.8, new[] { "ca", "fr", "it" }),
        ("ò", 1.0, new[] { "ca", "it" }),
        ("ì", 1.0, new[] { "it" }),
        ("ù", 0.8, new[] { "fr", "it" }),
        ("ï", 0.8, new[] { "ca", "fr", "nl" }),
        ("ë", 0.8, new[] { "fr", "nl" }),
        ("â", 0.8, new[] { "fr", "pt" }),
        ("ê", 0.8, new[] { "fr", "pt" }),
        ("ô", 0.8, new[] { "fr", "pt" }),
        ("î", 1.0, new[] { "fr" }),
        ("û", 1.0, new[] { "fr" }),
        ("á", 0.8, new[] { "es", "pt" }),
        ("í", 0.6, new[] { "es", "pt", "ca" }),
        ("ó", 0.6, new[] { "es", "pt", "ca" }),
        ("ú", 0.6, new[] { "es", "pt", "ca" }),
        ("é", 0.4, new[] { "es", "pt", "ca", "fr", "it" }),
    };

    private static void AddLetterEvidence(string lower, Dictionary<string, double> evidence)
    {
        if (lower.Contains("l·l") || lower.Contains('ŀ'))
            Add(evidence, 2.0, "ca");

        foreach (var (letters, weight, langs) in LetterFeatures)
        {
            if (!lower.Contains(letters)) continue;
            int present = langs.Count(evidence.ContainsKey);
            if (present == 0) continue;
            // Split like stop words, but a letter shared by many languages still counts a bit
            double share = weight / Math.Sqrt(present);
            foreach (var l in langs)
                if (evidence.ContainsKey(l)) evidence[l] += share;
        }
    }

    /// <summary>Word endings typical of one language (checked on accentless words, so OCR
    /// that drops accents and people who type without them still count).</summary>
    private static readonly (string ending, int minLength, double weight, string[] langs)[] Endings =
    {
        ("ciones", 7, 1.0, new[] { "es" }),
        ("cion", 5, 0.8, new[] { "es" }),
        ("sion", 5, 0.3, new[] { "es" }),
        ("dades", 6, 0.8, new[] { "es", "ca" }),
        ("idad", 5, 0.8, new[] { "es" }),
        ("ando", 5, 0.4, new[] { "es", "pt", "it" }),
        ("iendo", 6, 0.8, new[] { "es" }),
        ("mente", 7, 0.4, new[] { "es", "pt", "it" }),
        ("cao", 5, 0.8, new[] { "pt" }),
        ("coes", 6, 1.0, new[] { "pt" }),
        ("zione", 6, 1.0, new[] { "it" }),
        ("zioni", 6, 1.0, new[] { "it" }),
        ("cio", 5, 0.4, new[] { "ca" }),
        ("ment", 6, 0.3, new[] { "ca", "fr", "en" }),
        ("ing", 5, 0.6, new[] { "en" }),
        ("ings", 6, 0.6, new[] { "en" }),
        ("ness", 6, 0.8, new[] { "en" }),
        ("ship", 6, 0.6, new[] { "en" }),
        ("ght", 4, 0.6, new[] { "en" }),
        ("ly", 5, 0.4, new[] { "en" }),
        ("eux", 4, 0.8, new[] { "fr" }),
        ("eur", 5, 0.3, new[] { "fr" }),
        ("ung", 5, 0.8, new[] { "de" }),
        ("lich", 6, 0.8, new[] { "de", "nl" }),
        ("heid", 6, 1.0, new[] { "nl" }),
        ("tje", 5, 1.0, new[] { "nl" }),
    };

    private static void AddEndingEvidence(string folded, Dictionary<string, double> evidence)
    {
        foreach (var (ending, minLength, weight, langs) in Endings)
        {
            if (folded.Length < minLength || !folded.EndsWith(ending, StringComparison.Ordinal)) continue;
            Add(evidence, weight, langs);
            break; // longest/most specific endings are listed first
        }
    }

    private static void Add(Dictionary<string, double> evidence, double weight, params string[] langs)
    {
        var present = langs.Where(evidence.ContainsKey).ToList();
        foreach (var l in present) evidence[l] += weight / present.Count;
    }

    // ─── Common short words (accentless) ─────────────────────────

    private static HashSet<string> Words(string list) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Fold), StringComparer.Ordinal);

    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["en"] = Words("the and of to in is it you that was for on are with as his they be at have this from or had by not but what all were we when your can there an which she do how their if will up about out then them these so some her would like him into has more no could my than been who its now our just also only after me should it's don't doesn't isn't can't won't i'm you're we're they're that's there's let's i've i'll you'll here where why yes please thanks thank does did get got see use click open file new any each other because very well into over such through before while being both under again same few most those must might shall may yet off down here's he she's we'll they'll their's ours yours hers mine let"),
        ["es"] = Words("el la de que y en un una se no por con su para como le lo los las del al es son esta estan hay fue pero mas este ese esa si me ya muy sin sobre mi yo tambien hasta entre asi desde eso ni nos ella ellos ellas bien donde ahora despues siempre nada cada cuando porque tiene tienen puede pueden todo todos toda todas el usted ustedes aqui algo alguien hola gracias estas estoy estamos soy eres somos fueron era eran sido ser estar hace hacer hacia quiero quieres tengo tenemos voy vamos van mucho muchos mucha poco otro otra otros otras sus tu te ti les unos unas aunque mientras entonces luego tan tanto cual cuales quien quienes cuanto donde contra durante segun mismo misma tambien todavia aun solo nuestro nuestra vuestro esto aquel aquella esos esas estos haber habia han ha he hemos debe deben seria sera pues claro vez veces dia dias hoy manana ayer"),
        ["ca"] = Words("el la de que i a en un una es amb per els les del dels al als no com mes pero aquest aquesta aquests aquestes son hi ha seu seva seus seves tambe molt molts molta quan on que perque fins des sense tot tots tota totes fer esta estan aqui aixi doncs mateix cada altre altra altres nosaltres vosaltres jo ell ella ells elles meu meva teu teva si ja ara despres abans sobre entre dins gracies bon bona ho li em et ens us l' d' s' n' m' t' hem heu han he era eren sigui ser estat poden pot puc vull tenim tenen fa feia res algu alguna algun aquell aquella mentre doncs encara nomes sempre avui dema ahir llavors cap gaire"),
        ["fr"] = Words("le la les de des du un une et est en que qui dans pour pas sur au aux avec ce cette ces il elle ils elles nous vous je tu on ne se sa son ses leur leurs mais ou par plus tout tous toute sont ont etre avoir fait faire comme aussi bien tres peu sans sous chez entre depuis encore deja toujours jamais rien quand alors donc car si y l' d' c' j' n' qu' s' m' t' c'est n'est qu'il cela ceci ici oui non merci votre notre mon ma mes ton ta tes lui eux moi toi etait avait sera peut doit"),
        ["pt"] = Words("o a os as de do da dos das um uma e em no na nos nas que se por para com nao sim mais mas como ele ela eles elas voce voces eu nos seu sua seus suas este esta estes estas esse essa isso isto aquilo foi ser estar tem tinha ter sao estao ja muito muita muitos tambem quando onde porque entao ainda sobre entre depois antes ate sem pelo pela pelos pelas ao aos num numa obrigado obrigada ola meu minha teu tua aqui ali agora sempre nunca nada tudo todo toda todos todas outro outra cada mesmo lhe lhes"),
        ["it"] = Words("il lo la i gli le di da in con su per tra fra un uno una e che non si come ma piu anche sono era essere stato ha hanno ho abbiamo questo questa questi queste quello quella del della dei degli delle al alla ai agli alle dal dalla nel nella nei nelle sul sulla mi ti ci vi ne io tu lui lei noi voi loro mio mia suo sua tutto tutti molto poco ancora gia sempre mai quando dove perche cosi allora oggi grazie buongiorno ciao sei siamo fare fatto ogni"),
        ["de"] = Words("der die das den dem des und ist ein eine einen einem einer nicht mit von zu auf fur im in sich es ich du er sie wir ihr sind war waren hat haben wird werden auch als an aus bei nach uber unter vor wie wenn aber oder nur noch schon sehr kann konnen muss mussen dass so dann hier dort jetzt immer mein dein sein ihre unser kein keine man was wer wo warum danke bitte ja nein zum zur vom"),
        ["nl"] = Words("de het een en van in is dat die op te met voor zijn niet aan er maar om ook als bij of door naar uit dan nog wel geen meer kan zal wordt worden heeft hebben was waren ik je jij hij zij wij jullie ze ons mijn zijn haar hun dit deze daar hier wat wie waar waarom hoe nu al tot over onder dank alstublieft ja nee veel zeer"),
    };

    private static readonly HashSet<string> AllStopWords =
        new(StopWords.Values.SelectMany(s => s), StringComparer.Ordinal);

    // ─── Letter-trigram word model ──────────────────────────────

    /// <summary>
    /// The language whose trigram model fits <paramref name="word"/> clearly best, else null.
    /// </summary>
    private static string? TrigramWinner(string word, List<string> candidates)
    {
        string padded = "_" + word.Replace("'", "") + "_";
        if (padded.Length < 4) return null;
        string? best = null;
        double bestScore = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var lang in candidates)
        {
            if (!TrigramModels.TryGetValue(lang, out var model)) continue;
            double sum = 0;
            int n = 0;
            for (int i = 0; i + 3 <= padded.Length; i++)
            {
                model.counts.TryGetValue(padded.Substring(i, 3), out int c);
                sum += Math.Log((c + 0.5) / (model.total + 0.5 * 20000));
                n++;
            }
            double avg = sum / n;
            if (avg > bestScore) { second = bestScore; bestScore = avg; best = lang; }
            else if (avg > second) second = avg;
        }
        return best != null && bestScore - second >= TrigramMargin ? best : null;
    }

    private static readonly Dictionary<string, (Dictionary<string, int> counts, int total)> TrigramModels =
        TrainingText.ToDictionary(kv => kv.Key, kv => BuildModel(kv.Value));

    private static (Dictionary<string, int>, int) BuildModel(string sample)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (Match m in Regex.Matches(Fold(sample), @"\p{L}+"))
        {
            string padded = "_" + m.Value + "_";
            for (int i = 0; i + 3 <= padded.Length; i++)
            {
                var t = padded.Substring(i, 3);
                counts[t] = counts.GetValueOrDefault(t) + 1;
                total++;
            }
        }
        return (counts, total);
    }

    /// <summary>Short, everyday prose per language (written for this model), the source of
    /// the letter-trigram statistics.</summary>
    private static Dictionary<string, string> TrainingText => new()
    {
        ["en"] = "Reading on a screen should feel easy. When you open a document, the program looks for the paragraph under the mouse and starts speaking right away. Settings let you change the voice, the speed and the colours of the highlight. Something went wrong while downloading the update; please check your internet connection and try again later. The weather was nice yesterday, so we walked through the park and talked about the things we wanted to build this year. Thanks for writing back so quickly. I think the new version works much better, although the window still flickers when I switch between tabs. Would you like to schedule a meeting next week? Everything is ready for the presentation: the slides, the notes and the recording. Children were playing outside while their parents watched from the bench. Understanding how something works is usually the first step toward making it better. Keyboard shortcuts, accessibility features and privacy options are available in the menu on the right. Although the evening was cold, the light through the window was warm and bright. Download, install, restart and enjoy the improvements without losing your previous work.",
        ["es"] = "Leer en la pantalla debería ser fácil. Cuando abres un documento, el programa busca el párrafo que está debajo del ratón y empieza a hablar enseguida. En la configuración puedes cambiar la voz, la velocidad y los colores del resaltado. Algo ha fallado al descargar la actualización; comprueba tu conexión a internet y vuelve a intentarlo más tarde. Ayer hizo buen tiempo, así que fuimos andando por el parque y hablamos de las cosas que queríamos construir este año. Gracias por contestar tan rápido. Creo que la nueva versión funciona mucho mejor, aunque la ventana todavía parpadea cuando cambio de pestaña. ¿Te parece bien que quedemos la semana que viene? Todo está preparado para la presentación: las diapositivas, las notas y la grabación. Los niños jugaban fuera mientras sus padres los miraban desde el banco. Entender cómo funciona algo suele ser el primer paso para mejorarlo. Los atajos de teclado, las opciones de accesibilidad y de privacidad están en el menú de la derecha. Aunque la noche era fría, la luz que entraba por la ventana era cálida. Descargar, instalar, reiniciar y disfrutar de las mejoras sin perder el trabajo anterior. La información, la educación, la comunicación y la universidad son necesidades de la sociedad.",
        ["ca"] = "Llegir a la pantalla hauria de ser fàcil. Quan obres un document, el programa busca el paràgraf que hi ha sota el ratolí i comença a parlar de seguida. A la configuració pots canviar la veu, la velocitat i els colors del ressaltat. Alguna cosa ha fallat en baixar l'actualització; comprova la connexió a internet i torna-ho a provar més tard. Ahir va fer bon temps, així que vam anar caminant pel parc i vam parlar de les coses que volíem construir aquest any. Gràcies per contestar tan ràpid. Crec que la nova versió funciona molt millor, tot i que la finestra encara parpelleja quan canvio de pestanya. Et sembla bé que quedem la setmana que ve? Tot està preparat per a la presentació: les diapositives, les notes i l'enregistrament. Els nens jugaven a fora mentre els seus pares els miraven des del banc. Entendre com funciona una cosa sol ser el primer pas per millorar-la. La informació, l'educació i la comunicació són necessitats de la societat.",
        ["fr"] = "Lire sur un écran devrait être facile. Quand vous ouvrez un document, le programme cherche le paragraphe sous la souris et commence à parler tout de suite. Les réglages permettent de changer la voix, la vitesse et les couleurs de la surbrillance. Un problème est survenu pendant le téléchargement de la mise à jour; vérifiez votre connexion internet et réessayez plus tard. Hier il faisait beau, alors nous nous sommes promenés dans le parc et nous avons parlé des choses que nous voulions construire cette année. Merci d'avoir répondu si vite. Je pense que la nouvelle version fonctionne beaucoup mieux, même si la fenêtre clignote encore quand je change d'onglet. Les enfants jouaient dehors pendant que leurs parents les regardaient depuis le banc. Comprendre comment fonctionne quelque chose est souvent la première étape pour l'améliorer.",
        ["pt"] = "Ler no ecrã deveria ser fácil. Quando abre um documento, o programa procura o parágrafo que está debaixo do rato e começa a falar logo. Nas definições pode mudar a voz, a velocidade e as cores do destaque. Algo correu mal ao transferir a atualização; verifique a sua ligação à internet e tente novamente mais tarde. Ontem estava bom tempo, por isso fomos a pé pelo parque e falámos das coisas que queríamos construir este ano. Obrigado por responder tão depressa. Acho que a nova versão funciona muito melhor, embora a janela ainda pisque quando mudo de separador. As crianças brincavam lá fora enquanto os pais as observavam do banco. Perceber como uma coisa funciona costuma ser o primeiro passo para a melhorar. Não há informação, educação nem comunicação sem atenção.",
        ["it"] = "Leggere sullo schermo dovrebbe essere facile. Quando apri un documento, il programma cerca il paragrafo sotto il mouse e comincia a parlare subito. Nelle impostazioni puoi cambiare la voce, la velocità e i colori dell'evidenziazione. Qualcosa è andato storto durante il download dell'aggiornamento; controlla la connessione a internet e riprova più tardi. Ieri faceva bel tempo, così abbiamo camminato nel parco e abbiamo parlato delle cose che volevamo costruire quest'anno. Grazie per aver risposto così in fretta. Penso che la nuova versione funzioni molto meglio, anche se la finestra lampeggia ancora quando cambio scheda. I bambini giocavano fuori mentre i genitori li guardavano dalla panchina. Capire come funziona qualcosa è spesso il primo passo per migliorarlo.",
        ["de"] = "Lesen am Bildschirm sollte einfach sein. Wenn du ein Dokument öffnest, sucht das Programm den Absatz unter der Maus und beginnt sofort zu sprechen. In den Einstellungen kannst du die Stimme, die Geschwindigkeit und die Farben der Hervorhebung ändern. Beim Herunterladen der Aktualisierung ist etwas schiefgegangen; prüfe deine Internetverbindung und versuche es später noch einmal. Gestern war schönes Wetter, also sind wir durch den Park gelaufen und haben über die Dinge gesprochen, die wir dieses Jahr bauen wollten. Danke für die schnelle Antwort. Ich glaube, die neue Version funktioniert viel besser, obwohl das Fenster immer noch flackert, wenn ich den Tab wechsle.",
        ["nl"] = "Lezen op een scherm zou makkelijk moeten zijn. Als je een document opent, zoekt het programma de alinea onder de muis en begint het meteen te praten. In de instellingen kun je de stem, de snelheid en de kleuren van de markering veranderen. Er ging iets mis bij het downloaden van de update; controleer je internetverbinding en probeer het later opnieuw. Gisteren was het mooi weer, dus we liepen door het park en praatten over de dingen die we dit jaar wilden bouwen. Bedankt voor het snelle antwoord. Ik denk dat de nieuwe versie veel beter werkt, hoewel het venster nog steeds knippert als ik van tabblad wissel.",
    };
}
