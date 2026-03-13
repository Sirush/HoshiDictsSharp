# hoshidicts-sharp

C# (.NET 9.0) port of [hoshidicts](https://github.com/Manhhao/hoshidicts) by [Manhhao](https://github.com/Manhhao) — a high-performance Yomitan dictionary importer, query engine, and lookup system.

## Features

- **Import** Yomitan dictionary zips blazingly fast into a compact binary format
- **Query** imported dictionaries by expression or reading
- **Lookup** with automatic deconjugation and hiragana/katakana conversion
- **Frequency & pitch accent** support from meta dictionaries

## Building

```bash
dotnet build -c Release
```

## Publishing

### Standard (JIT)
```bash
dotnet publish cli/HoshiDictsSharpCli/HoshiDictsSharpCli.csproj -c Release -o publish
```

### NativeAOT

Requires **Visual Studio C++ Build Tools** (Desktop development with C++ workload).

From a **Developer Command Prompt for VS 2022**:
```bash
dotnet publish cli/HoshiDictsSharpCli/HoshiDictsSharpCli.csproj -c Release -o publish-aot
```

## CLI Usage

```bash
# Import a Yomitan dictionary
hoshidicts import "path/to/dictionary.zip"

# Query a term
hoshidicts query "path/to/imported/dict" "読む"

# Full lookup with deconjugation (e.g. 読んでいる → 読む)
hoshidicts lookup "path/to/dict" "読んでいる"

# Deconjugate a word
hoshidicts deconjugate "読んだ"

# Hiragana/katakana preprocessing
hoshidicts preprocess "カタカナ"

# Query frequency data
hoshidicts freq "path/to/freq_dict" "読む" "よむ"
```

## Tests

```bash
dotnet test -c Release
```

## Acknowledgements

[Hoshidicts](https://github.com/Manhhao/hoshidicts/tree/main-mit) original C++ version | MIT
[Yomitan](https://github.com/yomidevs/yomitan) dictionary format | GPLv3
[K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) high-performance LZ4 compression library | MIT
[Jiten](https://github.com/Sirush/Jiten) deconjugator | Apache-2.0