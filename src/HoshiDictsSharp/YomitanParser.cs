using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HoshiDictsSharp;

public static class YomitanParser
{
    public static string ParseIndexTitle(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("title"u8))
            {
                reader.Read();
                return reader.GetString()!;
            }
        }

        return "";
    }

    public static string SerializeIndex(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            doc.RootElement.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public readonly ref struct TermFields
    {
        public readonly int ExprStart, ExprLen;
        public readonly int ReadingStart, ReadingLen;
        public readonly int DefTagsStart, DefTagsLen;
        public readonly int RulesStart, RulesLen;
        public readonly int GlossaryStart, GlossaryLen;
        public readonly int TermTagsStart, TermTagsLen;

        public TermFields(int exprStart, int exprLen, int readingStart, int readingLen,
            int defTagsStart, int defTagsLen, int rulesStart, int rulesLen,
            int glossaryStart, int glossaryLen, int termTagsStart, int termTagsLen)
        {
            ExprStart = exprStart; ExprLen = exprLen;
            ReadingStart = readingStart; ReadingLen = readingLen;
            DefTagsStart = defTagsStart; DefTagsLen = defTagsLen;
            RulesStart = rulesStart; RulesLen = rulesLen;
            GlossaryStart = glossaryStart; GlossaryLen = glossaryLen;
            TermTagsStart = termTagsStart; TermTagsLen = termTagsLen;
        }
    }

    public readonly ref struct MetaFields
    {
        public readonly int ExprStart, ExprLen;
        public readonly int ModeStart, ModeLen;
        public readonly int DataStart, DataLen;

        public MetaFields(int exprStart, int exprLen, int modeStart, int modeLen, int dataStart, int dataLen)
        {
            ExprStart = exprStart; ExprLen = exprLen;
            ModeStart = modeStart; ModeLen = modeLen;
            DataStart = dataStart; DataLen = dataLen;
        }
    }

    public delegate void TermCallback(in TermFields fields);
    public delegate void MetaCallback(in MetaFields fields);

    public static unsafe void ParseTermBank(byte[] json, TermCallback callback)
        => ParseTermBank(json, json.Length, callback);

    public static unsafe void ParseTermBank(byte[] json, int len, TermCallback callback)
    {
        fixed (byte* basePtr = json)
        {
            byte* end = basePtr + len;
            byte* p = SkipWs(basePtr, end);
            if (p >= end || *p != (byte)'[') return;
            p++;

            while (p < end)
            {
                p = SkipWs(p, end);
                if (p >= end) break;

                byte c = *p;
                if (c == (byte)']') break;
                if (c == (byte)',') { p++; continue; }
                if (c != (byte)'[') { p++; continue; }
                p++;

                int exprStart, exprLen, readingStart, readingLen, defTagsStart, defTagsLen;
                int rulesStart, rulesLen, glossaryStart, glossaryLen, termTagsStart, termTagsLen;

                ReadStr(ref p, end, basePtr, out exprStart, out exprLen);
                p = SkipComma(p, end);
                ReadStr(ref p, end, basePtr, out readingStart, out readingLen);
                p = SkipComma(p, end);
                ReadStrOrNull(ref p, end, basePtr, out defTagsStart, out defTagsLen);
                p = SkipComma(p, end);
                ReadStr(ref p, end, basePtr, out rulesStart, out rulesLen);
                p = SkipComma(p, end);
                SkipVal(ref p, end);
                p = SkipComma(p, end);
                ReadRaw(ref p, end, basePtr, out glossaryStart, out glossaryLen);
                p = SkipComma(p, end);
                SkipVal(ref p, end);
                p = SkipComma(p, end);
                ReadStr(ref p, end, basePtr, out termTagsStart, out termTagsLen);

                p = SkipToClose(p, end);
                p++;

                callback(new TermFields(exprStart, exprLen, readingStart, readingLen,
                    defTagsStart, defTagsLen, rulesStart, rulesLen,
                    glossaryStart, glossaryLen, termTagsStart, termTagsLen));
            }
        }
    }

    public static unsafe void ParseMetaBank(byte[] json, MetaCallback callback)
        => ParseMetaBank(json, json.Length, callback);

    public static unsafe void ParseMetaBank(byte[] json, int len, MetaCallback callback)
    {
        fixed (byte* basePtr = json)
        {
            byte* end = basePtr + len;
            byte* p = SkipWs(basePtr, end);
            if (p >= end || *p != (byte)'[') return;
            p++;

            while (p < end)
            {
                p = SkipWs(p, end);
                if (p >= end) break;

                byte c = *p;
                if (c == (byte)']') break;
                if (c == (byte)',') { p++; continue; }
                if (c != (byte)'[') { p++; continue; }
                p++;

                ReadStr(ref p, end, basePtr, out int exprStart, out int exprLen);
                p = SkipComma(p, end);
                ReadStr(ref p, end, basePtr, out int modeStart, out int modeLen);
                p = SkipComma(p, end);
                ReadRaw(ref p, end, basePtr, out int dataStart, out int dataLen);

                p = SkipToClose(p, end);
                p++;

                callback(new MetaFields(exprStart, exprLen, modeStart, modeLen, dataStart, dataLen));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* SkipWs(byte* p, byte* end)
    {
        while (p < end && *p <= (byte)' ') p++;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* SkipComma(byte* p, byte* end)
    {
        p = SkipWs(p, end);
        if (p < end && *p == (byte)',') p++;
        return SkipWs(p, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ReadStr(ref byte* p, byte* end, byte* basePtr, out int start, out int length)
    {
        ReadStrOrNull(ref p, end, basePtr, out start, out length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ReadStrOrNull(ref byte* p, byte* end, byte* basePtr, out int start, out int length)
    {
        p = SkipWs(p, end);

        if (p < end && *p == (byte)'"')
        {
            p++;
            byte* strStart = p;

            while (p < end)
            {
                byte c = *p;
                if (c == (byte)'"')
                {
                    start = (int)(strStart - basePtr);
                    length = (int)(p - strStart);
                    p++;
                    return;
                }
                if (c == (byte)'\\') { p += 2; continue; }
                p++;
            }

            start = 0; length = 0;
            return;
        }

        if (p + 3 < end && *p == (byte)'n')
        {
            p += 4;
            start = 0; length = 0;
            return;
        }

        SkipVal(ref p, end);
        start = 0; length = 0;
    }

    private static unsafe void ReadRaw(ref byte* p, byte* end, byte* basePtr, out int start, out int length)
    {
        p = SkipWs(p, end);
        if (p >= end) { start = 0; length = 0; return; }

        byte* rawStart = p;
        byte c = *p;

        if (c == (byte)'[' || c == (byte)'{')
        {
            byte open = c;
            byte close = (byte)(c == (byte)'[' ? ']' : '}');
            int depth = 1;
            p++;
            bool inStr = false;

            while (p < end && depth > 0)
            {
                byte b = *p;
                if (inStr)
                {
                    if (b == (byte)'\\') { p += 2; continue; }
                    if (b == (byte)'"') inStr = false;
                }
                else
                {
                    if (b == (byte)'"') inStr = true;
                    else if (b == open) depth++;
                    else if (b == close) depth--;
                }
                p++;
            }

            start = (int)(rawStart - basePtr);
            length = (int)(p - rawStart);
            return;
        }

        if (c == (byte)'"')
        {
            p++;
            while (p < end)
            {
                byte b = *p;
                if (b == (byte)'\\') { p += 2; continue; }
                if (b == (byte)'"') { p++; break; }
                p++;
            }
            start = (int)(rawStart - basePtr);
            length = (int)(p - rawStart);
            return;
        }

        while (p < end)
        {
            byte b = *p;
            if (b == (byte)',' || b == (byte)']' || b == (byte)'}' || b <= (byte)' ')
                break;
            p++;
        }

        start = (int)(rawStart - basePtr);
        length = (int)(p - rawStart);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void SkipVal(ref byte* p, byte* end)
    {
        ReadRaw(ref p, end, p, out _, out _);
    }

    private static unsafe byte* SkipToClose(byte* p, byte* end)
    {
        p = SkipWs(p, end);
        while (p < end)
        {
            byte c = *p;
            if (c == (byte)']') return p;
            if (c == (byte)',')
            {
                p++;
                SkipVal(ref p, end);
                p = SkipWs(p, end);
                continue;
            }
            break;
        }
        return p;
    }
}
