// Compile on the build host; execute only inside the Windows test VM.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "NookShellPreview.h"
#include <shlobj.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <atomic>

namespace {
using GetClass = HRESULT (WINAPI*)(REFCLSID,REFIID,void**);
using CanUnload = HRESULT (WINAPI*)();
GetClass getClass = nullptr;
CanUnload canUnload = nullptr;
unsigned passed = 0,failed = 0;
void Check(bool condition,const char* name,HRESULT hr = S_OK) {
    std::printf("{\"test\":\"%s\",\"passed\":%s,\"hr\":\"0x%08lx\"}\n",name,condition ? "true" : "false",static_cast<unsigned long>(hr));
    condition ? ++passed : ++failed;
}
template<class T> struct Com {
    T* p = nullptr;
    ~Com() { if (p) p->Release(); }
    T** put() { return &p; }
    T* operator->() { return p; }
};
HRESULT Make(REFCLSID clsid,REFIID iid,void** output) {
    *output = nullptr;
    Com<IClassFactory> factory;
    HRESULT hr = getClass(clsid,IID_IClassFactory,reinterpret_cast<void**>(factory.put()));
    return FAILED(hr) ? hr : factory->CreateInstance(nullptr,iid,output);
}
HRESULT RenderBytes(const std::string& xml,UINT side,HBITMAP* bitmap,WTS_ALPHATYPE* alpha,bool repeat = false) {
    *bitmap = nullptr; *alpha = WTSAT_UNKNOWN;
    Com<IInitializeWithStream> initializer;
    HRESULT hr = Make(CLSID_NookSvgThumbnail,__uuidof(IInitializeWithStream),reinterpret_cast<void**>(initializer.put()));
    if (FAILED(hr)) return hr;
    Com<IStream> stream;
    stream.p = SHCreateMemStream(reinterpret_cast<const BYTE*>(xml.data()),static_cast<UINT>(xml.size()));
    if (!stream.p) return E_OUTOFMEMORY;
    if (FAILED(hr = initializer->Initialize(stream.p,STGM_READ))) return hr;
    Com<IThumbnailProvider> provider;
    if (FAILED(hr = initializer->QueryInterface(__uuidof(IThumbnailProvider),reinterpret_cast<void**>(provider.put())))) return hr;
    if (repeat) {
        HBITMAP first = nullptr; WTS_ALPHATYPE firstAlpha{};
        hr = provider->GetThumbnail(side,&first,&firstAlpha);
        if (first) DeleteObject(first);
        if (FAILED(hr)) return hr;
    }
    return provider->GetThumbnail(side,bitmap,alpha);
}
const char* valid = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\" viewBox=\"0 0 80 40\"><style>.shape{fill:#d02040}</style><rect class=\"shape\" x=\"10\" y=\"5\" width=\"60\" height=\"30\" rx=\"4\"/></svg>";
bool ActualPixels(HBITMAP bitmap,UINT width,UINT height) {
    if (!bitmap) return false;
    DIBSECTION data{};
    if (GetObjectW(bitmap,sizeof(data),&data) != sizeof(data) || !data.dsBm.bmBits || data.dsBm.bmWidth != static_cast<LONG>(width) || data.dsBm.bmHeight != static_cast<LONG>(height)) return false;
    const auto bytes = static_cast<const BYTE*>(data.dsBm.bmBits);
    const size_t center = static_cast<size_t>(height / 2) * data.dsBm.bmWidthBytes + (width / 2) * 4;
    return bytes[3] == 0 && bytes[center + 3] == 255 && bytes[center + 2] > 180 && bytes[center + 1] < 70 && bytes[center] < 100;
}
bool VisiblePixels(HBITMAP bitmap) {
    DIBSECTION data{};
    if (!bitmap || GetObjectW(bitmap,sizeof(data),&data) != sizeof(data) || !data.dsBm.bmBits || data.dsBm.bmBitsPixel != 32) return false;
    const auto bytes = static_cast<const BYTE*>(data.dsBm.bmBits);
    for (LONG y = 0; y < data.dsBm.bmHeight; ++y)
        for (LONG x = 0; x < data.dsBm.bmWidth; ++x)
            if (bytes[static_cast<size_t>(y) * data.dsBm.bmWidthBytes + x * 4 + 3] != 0) return true;
    return false;
}
void Positive(const char* name,const std::string& xml,bool expectKnownPixels = false,bool repeat = false) {
    HBITMAP bitmap = nullptr; WTS_ALPHATYPE alpha{};
    const ULONGLONG begin = GetTickCount64();
    HRESULT hr = RenderBytes(xml,256,&bitmap,&alpha,repeat);
    const bool pixels = VisiblePixels(bitmap) && (!expectKnownPixels || ActualPixels(bitmap,256,128));
    Check(SUCCEEDED(hr) && pixels && alpha == WTSAT_ARGB,name,hr);
    std::printf("{\"timing\":\"%s\",\"milliseconds\":%llu}\n",name,GetTickCount64() - begin);
    if (bitmap) DeleteObject(bitmap);
}
void Negative(const char* name,const std::string& xml,UINT side = 256) {
    HBITMAP bitmap = nullptr; WTS_ALPHATYPE alpha{};
    HRESULT hr = RenderBytes(xml,side,&bitmap,&alpha);
    Check(FAILED(hr) && !bitmap && alpha == WTSAT_UNKNOWN,name,hr);
    if (bitmap) DeleteObject(bitmap);
}
std::atomic<unsigned> concurrentOk{0},concurrentBusy{0},concurrentFailed{0};
HANDLE concurrentStart = nullptr;
DWORD WINAPI Concurrent(void*) {
    const HRESULT co = CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    WaitForSingleObject(concurrentStart,10000);
    HBITMAP bitmap = nullptr; WTS_ALPHATYPE alpha{};
    HRESULT hr = RenderBytes(valid,256,&bitmap,&alpha);
    if (SUCCEEDED(hr) && ActualPixels(bitmap,256,128) && alpha == WTSAT_ARGB) ++concurrentOk;
    else if (hr == HRESULT_FROM_WIN32(ERROR_BUSY)) ++concurrentBusy;
    else ++concurrentFailed;
    if (bitmap) DeleteObject(bitmap);
    if (SUCCEEDED(co)) CoUninitialize();
    return 0;
}
}

int wmain(int argc,wchar_t** argv) {
    if (argc < 2) { std::fwprintf(stderr,L"Usage: VerifyShellPreview.exe <absolute DLL path> [actual SVG path]\n"); return 2; }
    const HRESULT co = CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    if (FAILED(co)) return 2;
    HMODULE module = LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) { Check(false,"load_native_dll",HRESULT_FROM_WIN32(GetLastError())); CoUninitialize(); return 1; }
    getClass = reinterpret_cast<GetClass>(GetProcAddress(module,"DllGetClassObject"));
    canUnload = reinterpret_cast<CanUnload>(GetProcAddress(module,"DllCanUnloadNow"));
    if (!getClass || !canUnload) { Check(false,"exports"); FreeLibrary(module); CoUninitialize(); return 1; }
    Check(!GetProcAddress(module,"DllRegisterServer") && !GetProcAddress(module,"DllUnregisterServer"),"no_self_registration_exports");
    Check(canUnload() == S_OK,"initially_unloadable");
    void* output = reinterpret_cast<void*>(1);
    HRESULT hr = getClass(CLSID_NULL,IID_IClassFactory,&output);
    Check(hr == CLASS_E_CLASSNOTAVAILABLE && !output,"unknown_class_clears_output",hr);
    {
        Com<IClassFactory> factory;
        hr = getClass(CLSID_NookSvgThumbnail,IID_IClassFactory,reinterpret_cast<void**>(factory.put()));
        Check(SUCCEEDED(hr) && factory.p && canUnload() == S_FALSE,"factory_keeps_module_loaded",hr);
        output = reinterpret_cast<void*>(1);
        hr = factory->CreateInstance(nullptr,IID_IExtractIconW,&output);
        Check(hr == E_NOINTERFACE && !output,"thumbnail_does_not_expose_icon",hr);
        output = reinterpret_cast<void*>(1);
        hr = factory->CreateInstance(factory.p,__uuidof(IThumbnailProvider),&output);
        Check(hr == CLASS_E_NOAGGREGATION && !output,"aggregation_rejected",hr);
    }
    {
        Com<IExtractIconW> icon;
        hr = Make(CLSID_NookFileIcon,IID_IExtractIconW,reinterpret_cast<void**>(icon.put()));
        Check(SUCCEEDED(hr) && icon.p,"create_icon",hr);
        if (icon.p) {
            output = reinterpret_cast<void*>(1);
            hr = icon->QueryInterface(__uuidof(IThumbnailProvider),&output);
            Check(hr == E_NOINTERFACE && !output,"icon_does_not_expose_thumbnail",hr);
            Com<IPersistFile> file;
            hr = icon->QueryInterface(IID_IPersistFile,reinterpret_cast<void**>(file.put()));
            Check(SUCCEEDED(hr) && file.p,"icon_persist_interface",hr);
            wchar_t path[32768]; int index = -1; UINT flags = 0;
            const wchar_t* extensions[] = {L"png",L"SvG",L"PDF",L"psd",L"mp4",L"webm",L"zip",L"unrecognized",L"exe"};
            const wchar_t* expected[] = {L"PNG.ico",L"SVG.ico",L"PDF.ico",L"PSD.ico",L"MP4.ico",L"WEBM.ico",L"file.ico",L"file.ico",L"app.ico"};
            for (unsigned i = 0; i < sizeof(extensions)/sizeof(extensions[0]); ++i) {
                const std::wstring filename = std::wstring(L"C:\\nonexistent\\document.") + extensions[i];
                hr = file->Load(filename.c_str(),STGM_READ);
                if (SUCCEEDED(hr)) hr = icon->GetIconLocation(0,path,32768,&index,&flags);
                const bool match = SUCCEEDED(hr) && wcscmp(PathFindFileNameW(path),expected[i]) == 0 && index == 0 && (flags & GIL_PERINSTANCE);
                Check(match,"format_icon_location_without_reading_document",hr);
                if (match) {
                    HICON large = nullptr,small = nullptr;
                    hr = icon->Extract(path,0,&large,&small,MAKELONG(64,16));
                    Check(SUCCEEDED(hr) && large && small,"format_icon_extract",hr);
                    if (large) DestroyIcon(large); if (small) DestroyIcon(small);
                }
            }
            wchar_t tiny[1] = {L'x'}; index = 99; flags = 0;
            hr = icon->GetIconLocation(0,tiny,1,&index,&flags);
            Check(hr == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) && tiny[0] == 0 && index == 0,"icon_buffer_too_small",hr);
            HICON foreignLarge = reinterpret_cast<HICON>(1),foreignSmall = reinterpret_cast<HICON>(1);
            hr = icon->Extract(L"\\\\example.invalid\\share\\untrusted.ico",0,&foreignLarge,&foreignSmall,MAKELONG(32,16));
            Check(hr == E_INVALIDARG && !foreignLarge && !foreignSmall,"icon_rejects_arbitrary_resource_path",hr);
        }
    }
    {
        Com<IThumbnailProvider> provider;
        hr = Make(CLSID_NookSvgThumbnail,__uuidof(IThumbnailProvider),reinterpret_cast<void**>(provider.put()));
        HBITMAP bitmap = reinterpret_cast<HBITMAP>(1); WTS_ALPHATYPE alpha = WTSAT_ARGB;
        hr = provider->GetThumbnail(128,&bitmap,&alpha);
        Check(hr == E_UNEXPECTED && !bitmap && alpha == WTSAT_UNKNOWN,"uninitialized_thumbnail_clears_outputs",hr);
        bitmap = reinterpret_cast<HBITMAP>(1);
        hr = provider->GetThumbnail(128,&bitmap,nullptr);
        Check(hr == E_POINTER && !bitmap,"null_alpha_clears_bitmap",hr);
    }
    Positive("css_class_real_pixels_and_alpha",valid,true);
    Positive("xml_declaration_outer_whitespace",std::string("<?xml version='1.0' encoding='UTF-8'?>\n  ") + valid + "\n  ",true);
    Positive("pretty_svg_with_xlink_namespace","<?xml version='1.0' encoding='UTF-8'?>\n<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 80 40'>\n <defs>\n  <style>\n   .shape{fill:#d02040}\n  </style>\n </defs>\n <title>Fixture</title>\n <g data-name='Layer 1'>\n  <rect class='shape' x='10' y='5' width='60' height='30' rx='4'/>\n </g>\n</svg>\n",true);
    Positive("same_stream_repeated_render",valid,true,true);
    Positive("internal_gradient", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><defs><linearGradient id=\"g\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient></defs><rect width=\"20\" height=\"10\" fill=\"url(#g)\"/></svg>");
    Positive("text_and_unicode", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\"><text x=\"4\" y=\"30\" font-size=\"24\">Nook &#x6587;&#x5B57;</text></svg>");
    Negative("reject_empty","");
    Negative("reject_malformed","<svg><");
    Negative("reject_non_svg","<html/>");
    Negative("reject_script","<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
    Negative("reject_dtd_entity","<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><svg xmlns='http://www.w3.org/2000/svg'><text>&x;</text></svg>");
    Negative("reject_external_href","<svg xmlns='http://www.w3.org/2000/svg'><use href='https://example.invalid/payload.svg#x'/></svg>");
    Negative("reject_encoded_external_href","<svg xmlns='http://www.w3.org/2000/svg'><use href='&#104;ttps://example.invalid/payload.svg#x'/></svg>");
    Negative("reject_external_image","<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///C:/Windows/x.png'/></svg>");
    Negative("reject_css_external_url","<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:url(https://example.invalid/x)}</style></svg>");
    Negative("reject_css_comment_split_url","<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:u<!--split-->rl(https://example.invalid/x)}</style></svg>");
    Negative("reject_css_token_comment_split_url","<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:u/**/rl(https://example.invalid/x)}</style></svg>");
    Negative("reject_css_import","<svg xmlns='http://www.w3.org/2000/svg'><style>@import 'https://example.invalid/x';</style></svg>");
    Negative("reject_css_escapes","<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:u\\72l(https://example.invalid/x)}</style></svg>");
    Negative("reject_animation","<svg xmlns='http://www.w3.org/2000/svg'><animate attributeName='x'/></svg>");
    Negative("reject_foreign_object","<svg xmlns='http://www.w3.org/2000/svg'><foreignObject/></svg>");
    Negative("reject_xml_base","<svg xmlns='http://www.w3.org/2000/svg' xml:base='https://example.invalid/'/>");
    Negative("reject_oversized",std::string(NookMaximumSvgBytes + 1,'x'));
    Negative("reject_zero_request",valid,0);
    Negative("reject_oversized_request",valid,NookMaximumThumbnailSide + 1);
    if (argc > 2) {
        Com<IStream> sample;
        hr = SHCreateStreamOnFileEx(argv[2],STGM_READ | STGM_SHARE_DENY_NONE,FILE_ATTRIBUTE_NORMAL,FALSE,nullptr,sample.put());
        std::string bytes;
        if (SUCCEEDED(hr)) {
            char block[32768]; ULONG read = 0;
            while ((hr = sample->Read(block,sizeof(block),&read)) == S_OK && read && bytes.size() <= NookMaximumSvgBytes) bytes.append(block,read);
            if (hr == S_FALSE && read) bytes.append(block,read);
            Positive("user_actual_svg",bytes);
        } else Check(false,"read_user_actual_svg",hr);
    }
    concurrentStart = CreateEventW(nullptr,TRUE,FALSE,nullptr);
    HANDLE workers[8]{}; unsigned count = 0;
    for (unsigned i = 0; i < 8; ++i) { HANDLE worker = CreateThread(nullptr,0,Concurrent,nullptr,0,nullptr); if (worker) workers[count++] = worker; }
    SetEvent(concurrentStart);
    if (count) WaitForMultipleObjects(count,workers,TRUE,INFINITE);
    for (unsigned i = 0; i < count; ++i) CloseHandle(workers[i]);
    CloseHandle(concurrentStart);
    Check(count == 8 && concurrentOk > 0 && concurrentOk + concurrentBusy == 8 && concurrentFailed == 0,"concurrent_render_limit");
    std::printf("{\"concurrency\":{\"success\":%u,\"busy\":%u,\"failed\":%u}}\n",concurrentOk.load(),concurrentBusy.load(),concurrentFailed.load());
    Check(canUnload() == S_OK,"all_objects_released");
    FreeLibrary(module); CoUninitialize();
    std::printf("{\"summary\":{\"passed\":%u,\"failed\":%u}}\n",passed,failed);
    return failed ? 1 : 0;
}
