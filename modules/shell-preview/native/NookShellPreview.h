#pragma once
#include <windows.h>

// These identities belong to Nook, never to resvg or another installed viewer.
// Registration is performed by the application after a successful installation;
// this DLL deliberately has no DllRegisterServer/DllUnregisterServer export.
inline constexpr CLSID CLSID_NookFileIcon =
    {0x612e3a5a,0x3c2c,0x4709,{0x8c,0x54,0x9c,0x3e,0xa9,0x27,0xc0,0x86}};
inline constexpr CLSID CLSID_NookSvgThumbnail =
    {0xfa45a458,0x130e,0x47ba,{0xaa,0xe0,0x12,0x02,0xd6,0x93,0x0a,0x79}};

inline constexpr unsigned long NookMaximumSvgBytes = 8UL * 1024 * 1024;
inline constexpr unsigned long NookMaximumPngBytes = 8UL * 1024 * 1024;
inline constexpr unsigned int NookMaximumThumbnailSide = 1024;
inline constexpr unsigned long NookRendererTimeoutMs = 3000;
// resvg lazily scans installed fonts only for SVGs containing real text nodes.
// Keep that cold-start allowance separate from the ordinary vector budget.
inline constexpr unsigned long NookTextRendererTimeoutMs = 8000;
