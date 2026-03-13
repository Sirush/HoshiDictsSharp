using System.Text;

namespace HoshiDictsSharp;

public static class TextProcessor
{
    public readonly record struct TextVariant(string Text, int Steps);

    const char HiraganaStart = '\u3041'; // ぁ
    const char HiraganaEnd = '\u3096';   // ゖ
    const char KatakanaStart = '\u30A1'; // ァ
    const char KatakanaEnd = '\u30F6';   // ヶ
    const int KanaOffset = KatakanaStart - HiraganaStart; // 0x60
    const char KatakanaSmallKa = '\u30F5'; // ヵ
    const char KatakanaSmallKe = '\u30F6'; // ヶ
    const char ProlongedSoundMark = '\u30FC'; // ー

    const string HiraganaARow = "ぁあかがさざただなはばぱまゃやらゎわゕ";
    const string HiraganaIRow = "ぃいきぎしじちぢにひびぴみりゐ";
    const string HiraganaURow = "ぅうくぐすずっつづぬふぶぷむゅゆるゔ";
    const string HiraganaERow = "ぇえけげせぜてでねへべぺめれゑゖ";
    const string HiraganaORow = "ぉおこごそぞとどのほぼぽもょよろを";

    static char GetProlongedHiragana(char c)
    {
        if (HiraganaARow.Contains(c)) return 'あ';
        if (HiraganaIRow.Contains(c)) return 'い';
        if (HiraganaURow.Contains(c)) return 'う';
        if (HiraganaERow.Contains(c)) return 'え';
        if (HiraganaORow.Contains(c)) return 'う';
        return '\0';
    }

    static string ToHiragana(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == ProlongedSoundMark && sb.Length > 0)
            {
                char prolonged = GetProlongedHiragana(sb[^1]);
                if (prolonged != '\0')
                {
                    sb.Append(prolonged);
                    continue;
                }
            }

            if (c != KatakanaSmallKa && c != KatakanaSmallKe && c >= KatakanaStart && c <= KatakanaEnd)
                sb.Append((char)(c - KanaOffset));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    static string ToKatakana(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= HiraganaStart && c <= HiraganaEnd)
                sb.Append((char)(c + KanaOffset));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static List<TextVariant> Process(string src)
    {
        var result = new List<TextVariant>(3) { new(src, 0) };

        string hiragana = ToHiragana(src);
        string katakana = ToKatakana(src);

        if (hiragana != src)
            result.Add(new(hiragana, 1));

        if (katakana != src && katakana != hiragana)
            result.Add(new(katakana, 1));

        return result;
    }
}
