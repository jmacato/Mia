# Mia

Mia emulates the Sony Ericsson T68i handset in C# with Avalonia and .NET. It runs the original AVR handset firmware and its ARM modem firmware. The project includes desktop, browser, and command-line front ends.

This repository does not include handset firmware, modem firmware, a GDFS
image, or service documentation. You must supply those files from hardware or
media that you can lawfully use.

## Required inputs

The front ends use these default paths:

- `flat.bin` for the AVR handset firmware
- `images/T68i_Default_GDFS.raw` for the GDFS image
- `images/t68i_R8A015_125326_Modem.bih` for the R8A015 ARM modem firmware

The desktop head also accepts `images/T68i_Full_GDFS.raw` and
`images/T68i_Full_GDFS.compact.raw`. You can use command-line options to select
files at other locations.

## Build

Install the .NET SDK version in [`global.json`](global.json). Then build the
emulation core:

```sh
dotnet build src/Mia.Emulator/Mia.Emulator.csproj -c Release
```

The desktop and browser projects use Avalonia 12.1.1 packages from NuGet.org.
Install the `wasm-tools` workload to build the browser project.

Build the complete solution:

```sh
dotnet build Mia.slnx -c Release
```

## Run

Run the desktop head from the repository root:

```sh
dotnet run -c Release --project src/Mia.Desktop -- \
  --firmware-file path/to/flat.bin \
  --gdfs-file path/to/T68i_Full_GDFS.raw \
  --modem-file path/to/t68i_R8A015_125326_Modem.bih
```

The desktop head starts the handset and provides a keypad, a live LCD, and a
loopback automation interface.

The command-line emulator exposes trace and capture options:

```sh
dotnet run -c Release --project src/Mia.Emulator.Cli -- \
  path/to/flat.bin \
  --gdfs-file path/to/T68i_Full_GDFS.raw \
  --modem-file path/to/t68i_R8A015_125326_Modem.bih \
  --virtual-sim
```

Run the command with `--help` to see all options.

## Test

The ARM and AVR core tests do not need the proprietary firmware files:

```sh
dotnet test tests/Arm7Core.Tests/Arm7Core.Tests.csproj -c Release
dotnet test tests/AvrCore.Tests/AvrCore.Tests.csproj -c Release
```

The application tests use the same public Avalonia packages as the shared UI.

## Layout

- `src/Arm7Core` contains the ARM7TDMI interpreter.
- `src/AvrCore` contains the AVR interpreter and assembler.
- `src/Mia.Emulator` contains the handset, modem, ASIC, SIM, and GSM models.
- `src/Mia.App` contains the shared cross-platform Avalonia UI.
- `src/Mia.Desktop` contains the thin desktop head and its platform services.
- `src/Mia.Browser` contains the thin WebAssembly head and its browser services.
- `src/Mia.Emulator.Cli` contains the command-line head.
- `src/Mia.Android` and `src/Mia.iOS` are stubs. They do not launch applications.
- `tests` contains the automated tests.
- `tools` contains analysis, conversion, probe, and build utilities.

## License

The project code uses the MIT License. See [`LICENSE`](LICENSE).
Ported and third-party code has separate terms in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
The license does not cover proprietary handset files or marks. See
[`TRADEMARKS.md`](TRADEMARKS.md).
