# ADR 0001: MemoryPack Native AOT aggregate warnings

- Status: accepted
- Date: 2026-07-18

## Context

SlimVector uses MemoryPack's source generator for versioned internal binary data. MemoryPack 1.21.4 describes its generated path as Native AOT friendly, but `MemoryPack.Core` also ships generic runtime formatter fallback paths. The .NET 10 Native AOT analyzer examines those unused paths and emits aggregate `IL2104` and `IL3053` warnings for that assembly.

SlimVector calls only generic serializers for `[MemoryPackable]` types whose formatters are generated at compile time. Native AOT smoke tests exercise those calls.

## Decision

Keep trim and AOT analysis enabled and warnings-as-errors for the solution. In the API publish project only, do not promote the two aggregate MemoryPack assembly warnings (`IL2104` and `IL3053`) to errors. Do not suppress detailed SlimVector linker warnings.

Revisit this exception on every MemoryPack upgrade and remove it once the package annotations no longer produce the aggregate warnings.

On macOS, Apple's native linker may separately report that a temporary Clang module-cache `.pcm` file referenced by framework debug information no longer exists. This is an external debug-experience diagnostic, not a trim/AOT analysis warning and does not affect the produced executable. Linux container publication does not use that Apple toolchain.
