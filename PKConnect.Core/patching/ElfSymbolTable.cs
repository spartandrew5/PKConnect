namespace PKConnect.Core.patching;

/// <summary>
/// Minimal ELF32 (little-endian, ARM) symbol-table reader for a decomp build's
/// <c>.elf</c>. Used to resolve pokeemerald-expansion addresses, which move with
/// every build/config change - unlike retail revisions, there are no fixed
/// offsets to hardcode. Unlike the linker <c>.map</c>, the ELF also carries
/// file-local (<c>static</c>) symbols and every symbol's size.
/// </summary>
public sealed class ElfSymbolTable
{
    public sealed record Symbol(string Name, uint Address, uint Size, int SectionIndex);

    private readonly record struct Section(uint Type, uint Addr, uint Offset, uint Size, uint Link);

    private readonly Dictionary<string, Symbol> symbols;
    private readonly List<Section> sections;
    private readonly string path;

    private ElfSymbolTable(string path, Dictionary<string, Symbol> symbols, List<Section> sections)
    {
        this.path = path;
        this.symbols = symbols;
        this.sections = sections;
    }

    public string Path => path;

    /// <summary>Exact name, else a compiler-suffixed static (<c>name.lto_priv.0</c>, <c>name.123</c>).</summary>
    public Symbol? Get(string name)
    {
        if (symbols.TryGetValue(name, out var s))
        {
            return s;
        }
        var prefix = name + ".";
        return symbols.Values.FirstOrDefault(v => v.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>ROM file offset of a GBA ROM symbol (address - 0x08000000), or null.</summary>
    public int? RomOffset(string name) =>
        Get(name) is { } s && s.Address >= Gba.RomBase && s.Address < Gba.RomBase + 0x2000000
            ? (int)(s.Address - Gba.RomBase)
            : null;

    /// <summary>The symbol's initialised bytes as stored in the ELF (for checking the ROM came from this build).</summary>
    public byte[]? ReadBytes(Symbol symbol, int length)
    {
        if (symbol.SectionIndex <= 0 || symbol.SectionIndex >= sections.Count)
        {
            return null;
        }
        var sec = sections[symbol.SectionIndex];
        if (sec.Type == 8 /* SHT_NOBITS */ || symbol.Address < sec.Addr || symbol.Address + length > sec.Addr + sec.Size)
        {
            return null;
        }
        using var fs = File.OpenRead(path);
        fs.Position = sec.Offset + (symbol.Address - sec.Addr);
        var buf = new byte[length];
        fs.ReadExactly(buf);
        return buf;
    }

    public static ElfSymbolTable? TryLoad(string elfPath)
    {
        try
        {
            using var fs = File.OpenRead(elfPath);
            using var br = new BinaryReader(fs);

            var ident = br.ReadBytes(16);
            if (ident.Length < 16 || ident[0] != 0x7F || ident[1] != 'E' || ident[2] != 'L' || ident[3] != 'F'
                || ident[4] != 1 /* ELFCLASS32 */ || ident[5] != 1 /* little-endian */)
            {
                return null;
            }

            fs.Position = 0x20;
            var shoff = br.ReadUInt32();
            fs.Position = 0x2E;
            var shentsize = br.ReadUInt16();
            var shnum = br.ReadUInt16();

            var sections = new List<Section>(shnum);
            for (var i = 0; i < shnum; i++)
            {
                fs.Position = shoff + (long)i * shentsize;
                br.ReadUInt32(); // sh_name
                var type = br.ReadUInt32();
                br.ReadUInt32(); // sh_flags
                var addr = br.ReadUInt32();
                var offset = br.ReadUInt32();
                var size = br.ReadUInt32();
                var link = br.ReadUInt32();
                sections.Add(new(type, addr, offset, size, link));
            }

            var symtab = sections.FindIndex(s => s.Type == 2 /* SHT_SYMTAB */);
            if (symtab < 0)
            {
                return null;
            }
            var symSec = sections[symtab];
            var strSec = sections[(int)symSec.Link];

            fs.Position = strSec.Offset;
            var strtab = br.ReadBytes((int)strSec.Size);
            fs.Position = symSec.Offset;
            var symData = br.ReadBytes((int)symSec.Size);

            var result = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            for (var p = 0; p + 16 <= symData.Length; p += 16)
            {
                var nameOff = BitConverter.ToInt32(symData, p);
                var value = BitConverter.ToUInt32(symData, p + 4);
                var size = BitConverter.ToUInt32(symData, p + 8);
                var info = symData[p + 12];
                var shndx = BitConverter.ToUInt16(symData, p + 14);
                var kind = info & 0xF;
                if (nameOff <= 0 || nameOff >= strtab.Length || kind > 2 /* NOTYPE / OBJECT / FUNC */)
                {
                    continue;
                }
                if (kind == 2)
                {
                    value &= ~1u; // Thumb functions carry the interworking bit
                }
                var end = Array.IndexOf(strtab, (byte)0, nameOff);
                var name = System.Text.Encoding.ASCII.GetString(strtab, nameOff, (end < 0 ? strtab.Length : end) - nameOff);
                // First definition wins; the player-graphics symbols are unique.
                result.TryAdd(name, new(name, value, size, shndx));
            }

            return new ElfSymbolTable(elfPath, result, sections);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
