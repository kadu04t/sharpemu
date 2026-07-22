using Iced.Intel;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;

if (args.Length is not (3 or 4) ||
    !ulong.TryParse(args[1], System.Globalization.NumberStyles.HexNumber, null, out var start) ||
    !ulong.TryParse(args[2], System.Globalization.NumberStyles.HexNumber, null, out var stop) ||
    stop <= start)
{
    Console.Error.WriteLine("usage: disasm <file> <start-hex> <stop-hex>");
    return 1;
}

var memory = new VirtualMemory();
var imageData = File.ReadAllBytes(args[0]);
var image = new SelfLoader().Load(imageData, memory);
var bytes = new byte[checked((int)(stop - start))];
var runtimeStart = 0x800000000UL + start;
var region = image.MappedRegions.FirstOrDefault(candidate =>
    runtimeStart >= candidate.VirtualAddress &&
    runtimeStart + (ulong)bytes.Length <= candidate.VirtualAddress + candidate.FileSize);
if (region.FileSize == 0)
{
    Console.Error.WriteLine($"could not resolve mapped file data at 0x{runtimeStart:X16}");
    return 2;
}
var fileOffset = checked(region.FileOffset + runtimeStart - region.VirtualAddress);
imageData.AsSpan(checked((int)fileOffset), bytes.Length).CopyTo(bytes);

if (args.Length == 4)
{
    if (string.Equals(args[3], "raw", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(Convert.ToHexString(bytes));
        return 0;
    }

    var pattern = Convert.FromHexString(args[3]);
    for (var index = 0; index <= bytes.Length - pattern.Length; index++)
    {
        if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern))
        {
            Console.WriteLine($"{runtimeStart + (ulong)index:X16}");
        }
    }
    return 0;
}

var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
decoder.IP = runtimeStart;
var formatter = new IntelFormatter();
while (decoder.IP < 0x800000000UL + stop)
{
    decoder.Decode(out var instruction);
    if (instruction.Code == Code.INVALID)
    {
        Console.WriteLine($"{instruction.IP:X16}  <invalid>");
        continue;
    }

    var output = new StringOutput();
    formatter.Format(instruction, output);
    Console.WriteLine($"{instruction.IP:X16}  {output}");
}

return 0;
