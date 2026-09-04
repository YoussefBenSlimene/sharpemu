<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.LibAtrac9

A managed ATRAC9 audio decoder. ATRAC9 is the audio codec Sony uses on PS5;
the decoder is invoked from `SharpEmu.Libs` audio code.

This project originates from the VGMToolbox project (MIT license) and was
adapted into SharpEmu as a separate `net10.0` library. It has no dependency
on any other SharpEmu project — it is a leaf, consumed by `SharpEmu.Libs`.

`src/SharpEmu.LibAtrac9/SharpEmu.LibAtrac9.csproj` declares
`AssemblyName=SharpEmu.LibAtrac9`, `RootNamespace=LibAtrac9`. It is kept
external by the CLI's `KeepLibAtrac9External` MSBuild property so it can sit
in the `plugins/` folder next to the executable (and is shipped as part of the
release archives).

## File map

| File | Purpose |
| --- | --- |
| `Atrac9Decoder.cs` | The decoder entry point. `Initialize(byte[] configData)` parses the 4-byte ATRAC9 stream config; `Decode(byte[] atrac9Data, short[][] pcmOut)` decodes one superframe into 16-bit PCM (`[channels][superframeSamples]`). |
| `Atrac9Config.cs` | The ATRAC9 config parsed from the 4-byte header: sample rate, channel count, superframe size, frame size, superframe samples. |
| `Atrac9Rng.cs` | The ATRAC9 RNG used during decoding. |
| `Frame.cs` | A superframe (the unit the decoder processes). |
| `Block.cs` | A block within a frame. |
| `Channel.cs` | One channel's data within a block. |
| `ChannelConfig.cs` | Channel layout (mono/stereo/joint-stereo). |
| `BitAllocation.cs` | The bit-allocation step. |
| `BandExtension.cs` | Band-extension decoding. |
| `ScaleFactors.cs` | Scale-factor decoding. |
| `Quantization.cs` | Quantization step. |
| `HuffmanCodebook.cs` / `HuffmanCodebooks.cs` | Huffman tables used during decoding. |
| `Tables.cs` | Lookup tables. |
| `Stereo.cs` | Stereo processing. |
| `Unpack.cs` | Bit-unpacking utilities. |
| `Utilities/` | Bit-reader and other helpers. |

## Usage (from `SharpEmu.Libs`)

```csharp
using LibAtrac9;

var decoder = new Atrac9Decoder();
decoder.Initialize(configData);   // 4-byte ATRAC9 stream config
// ...
decoder.Decode(atrac9Data, pcmOut); // short[channels][superframeSamples]
```

`pcmOut` must have dimensions `Config.ChannelCount × Config.SuperframeSamples`;
`atrac9Data` must be at least `Config.SuperframeBytes` long. Throw an
`InvalidOperationException` if `Decode` is called before `Initialize`.

## Public API surface

The whole library lives in `namespace LibAtrac9` and exposes:

- `Atrac9Decoder` — the public decoder class.
- `Atrac9Config` — the parsed config.

Everything else is `internal`/`private` to the library.

## Dependencies

None (other than the BCL). Pure managed code.

## Related

- [SharpEmu.Libs guide](libs.md) — the audio libraries that consume this.
- [SharpEmu.CLI guide](cli.md) — `KeepLibAtrac9External` keeps this DLL out of
  the single-file bundle.
