#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Mia.Emulator;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/compact_gdfs.cs -- INPUT.raw OUTPUT.raw");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
if (inputPath == outputPath)
{
    Console.Error.WriteLine("Input and output paths must differ.");
    return 2;
}

try
{
    var result = GdfsCompactor.Compact(File.ReadAllBytes(inputPath));
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }
    File.WriteAllBytes(outputPath, result.Image);

    foreach (var block in result.Blocks)
    {
        Console.WriteLine(
            $"physical=0x{block.PhysicalBlock:x2} logical=0x{block.LogicalUnit:x2} " +
            $"entries={block.OriginalEntryCount}->{block.CurrentEntryCount} " +
            $"removed-bytes={block.RemovedProgrammedBytes:n0}");
    }
    Console.WriteLine(
        $"Compacted {result.Blocks.Count} active blocks; removed " +
        $"{result.RemovedEntryCount:n0} superseded entries and " +
        $"{result.RemovedProgrammedBytes:n0} programmed bytes.");
    Console.WriteLine(
        $"SHA-256: {Convert.ToHexString(SHA256.HashData(result.Image)).ToLowerInvariant()}");
    Console.WriteLine($"Output: {outputPath}");
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"Could not compact GDFS: {error.Message}");
    return 2;
}
