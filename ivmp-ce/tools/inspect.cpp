// SPDX-License-Identifier: GPL-3.0-only
#include "../client/CEBindings.h"
#include <cstdio>

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::fputs("usage: ivmp-ce-inspect.exe path-to-GTAIV.exe\n", stderr);
        return 2;
    }
    const auto file = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        std::fprintf(stderr, "Could not open executable (Win32 %lu)\n", GetLastError());
        return 2;
    }
    const auto mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY | SEC_IMAGE, 0, 0, nullptr);
    CloseHandle(file);
    if (!mapping)
    {
        std::fprintf(stderr, "Could not map executable (Win32 %lu)\n", GetLastError());
        return 2;
    }
    const auto base = static_cast<const std::uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
    CloseHandle(mapping);
    if (!base) return 2;

    const auto header = ivmp_ce::PeHeader(base);
    if (!ivmp_ce::IsCompleteEditionBuild(header))
    {
        if (header) std::printf("Other x86 image: timestamp 0x%08lx, image size 0x%08lx, entry 0x%08lx\n",
            header->FileHeader.TimeDateStamp, header->OptionalHeader.SizeOfImage, header->OptionalHeader.AddressOfEntryPoint);
        else std::fputs("Unknown or non-x86 PE image.\n", stderr);
        UnmapViewOfFile(base);
        return 1;
    }

    bool matched = true;
    for (const auto& signature : ivmp_ce::Signatures)
    {
        const auto length = (std::strlen(signature.bytes) + 1) / 3;
        const auto code = base + signature.rva;
        const bool valid = ivmp_ce::PageIsExecutable(code, length) && ivmp_ce::Matches(code, length, signature.bytes);
        std::printf("%-8s 0x%08x  %s\n", valid ? "matched" : "REJECTED", signature.rva, signature.name);
        matched = matched && valid;
    }
    matched = matched && static_cast<bool>(ivmp_ce::ResolveLoadedModule(reinterpret_cast<HMODULE>(const_cast<std::uint8_t*>(base))));
    UnmapViewOfFile(base);
    return matched ? 0 : 1;
}
