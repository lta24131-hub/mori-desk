#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "NookShellPreview.h"
#include <shlobj.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wincodec.h>
#include <xmllite.h>
#include <sddl.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <limits>
#include <new>
#include <string>
#include <vector>

namespace {
HMODULE moduleHandle = nullptr;
std::atomic<long> objects{0};
constexpr HRESULT InvalidData = HRESULT_FROM_WIN32(ERROR_INVALID_DATA);

template<class T> class ComPtr {
    T* value = nullptr;
public:
    ComPtr() = default;
    explicit ComPtr(T* p) : value(p) {}
    ~ComPtr() { if (value) value->Release(); }
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    T* get() const { return value; }
    T* operator->() const { return value; }
    T** put() { if (value) value->Release(); value = nullptr; return &value; }
    void reset(T* p = nullptr) { if (value) value->Release(); value = p; }
};

class Handle {
    HANDLE value = nullptr;
public:
    Handle() = default;
    explicit Handle(HANDLE h) : value(h) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value; }
    bool valid() const { return value && value != INVALID_HANDLE_VALUE; }
    void reset(HANDLE h = nullptr) { if (valid()) CloseHandle(value); value = h; }
};

HRESULT LastErrorResult() { DWORD e = GetLastError(); return HRESULT_FROM_WIN32(e ? e : ERROR_GEN_FAILURE); }
bool Same(const wchar_t* a, const wchar_t* b) { return a && b && wcscmp(a,b) == 0; }
bool EqualsAscii(const std::wstring& a, const wchar_t* b) { return _wcsicmp(a.c_str(),b) == 0; }
bool StartsAscii(const std::wstring& a, const wchar_t* b) { return _wcsnicmp(a.c_str(),b,wcslen(b)) == 0; }
bool Space(wchar_t ch) { return ch == L' ' || ch == L'\t' || ch == L'\r' || ch == L'\n' || ch == L'\f'; }

std::wstring ModuleDirectory() {
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(moduleHandle,path.data(),static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    std::wstring result(path.data(),length);
    auto slash = result.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : result.substr(0,slash);
}

bool OrdinaryFile(const std::wstring& path) {
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

// Icon extraction is deliberately independent of rendering. This object can
// live in Explorer: it only examines the filename and known icon resources.
class FileIcon final : public IExtractIconW, public IPersistFile {
    std::atomic<ULONG> references{1};
    std::wstring filename;
    std::wstring IconPath() const {
        std::wstring root = ModuleDirectory();
        if (root.empty()) return {};
        std::wstring extension;
        auto slash = filename.find_last_of(L"\\/");
        auto dot = filename.find_last_of(L'.');
        if (dot != std::wstring::npos && (slash == std::wstring::npos || dot > slash)) extension = filename.substr(dot + 1);
        if (filename.empty() || EqualsAscii(extension,L"exe")) return root + L"\\icons\\app.ico";
        bool valid = !extension.empty() && extension.size() <= 12;
        for (wchar_t& ch : extension) {
            if (ch >= L'a' && ch <= L'z') ch -= L'a' - L'A';
            if (!((ch >= L'A' && ch <= L'Z') || (ch >= L'0' && ch <= L'9'))) valid = false;
        }
        if (valid) {
            auto candidate = root + L"\\icons\\" + extension + L".ico";
            if (OrdinaryFile(candidate)) return candidate;
        }
        return root + L"\\icons\\file.ico";
    }
public:
    FileIcon() { ++objects; }
    ~FileIcon() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (IsEqualIID(id,IID_IUnknown) || IsEqualIID(id,IID_IExtractIconW)) *result = static_cast<IExtractIconW*>(this);
        else if (IsEqualIID(id,IID_IPersist) || IsEqualIID(id,IID_IPersistFile)) *result = static_cast<IPersistFile*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --references; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetClassID(CLSID* value) override { if (!value) return E_POINTER; *value = CLSID_NookFileIcon; return S_OK; }
    HRESULT STDMETHODCALLTYPE IsDirty() override { return S_FALSE; }
    HRESULT STDMETHODCALLTYPE Load(LPCOLESTR file,DWORD) override {
        try { if (!file || wcslen(file) >= 32768) return E_INVALIDARG; filename = file; return S_OK; }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE Save(LPCOLESTR,BOOL) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SaveCompleted(LPCOLESTR) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCurFile(LPOLESTR* file) override { if (!file) return E_POINTER; *file = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetIconLocation(UINT,LPWSTR path,UINT capacity,int* index,UINT* flags) noexcept override {
        try {
            if (path && capacity) path[0] = 0;
            if (index) *index = 0;
            if (flags) *flags = GIL_PERINSTANCE;
            if (!path || !capacity || !index || !flags) return E_POINTER;
            const auto icon = IconPath();
            if (icon.empty() || !OrdinaryFile(icon)) return E_FAIL;
            if (icon.size() + 1 > capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
            std::copy(icon.begin(),icon.end(),path); path[icon.size()] = 0;
            return S_OK;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE Extract(LPCWSTR path,UINT index,HICON* large,HICON* small,UINT size) noexcept override {
        if (large) *large = nullptr;
        if (small) *small = nullptr;
        // GetIconLocation names a real ICO file. LoadImage handles standalone
        // PNG-backed ICO frames; SHDefExtractIcon can report S_OK with empty
        // handles for these files. No document is opened here.
        if (!path || index != 0) return E_INVALIDARG;
        if (!large && !small) return E_POINTER;
        try {
            const std::wstring ownIcon = IconPath();
            // A COM caller cannot use this entry point to make Explorer parse
            // an arbitrary document, UNC icon, or executable on its behalf.
            if (ownIcon.empty() || _wcsicmp(path,ownIcon.c_str()) != 0 || !OrdinaryFile(ownIcon)) return E_INVALIDARG;
            const UINT largeSize = LOWORD(size) ? LOWORD(size) : static_cast<UINT>(GetSystemMetrics(SM_CXICON));
            const UINT smallSize = HIWORD(size) ? HIWORD(size) : static_cast<UINT>(GetSystemMetrics(SM_CXSMICON));
            if ((large && largeSize > NookMaximumThumbnailSide) || (small && smallSize > NookMaximumThumbnailSide)) return E_INVALIDARG;
            HICON largeIcon = large ? static_cast<HICON>(LoadImageW(nullptr,ownIcon.c_str(),IMAGE_ICON,largeSize,largeSize,LR_LOADFROMFILE)) : nullptr;
            if (large && !largeIcon) return LastErrorResult();
            HICON smallIcon = small ? static_cast<HICON>(LoadImageW(nullptr,ownIcon.c_str(),IMAGE_ICON,smallSize,smallSize,LR_LOADFROMFILE)) : nullptr;
            if (small && !smallIcon) { HRESULT hr = LastErrorResult(); if (largeIcon) DestroyIcon(largeIcon); return hr; }
            if (large) *large = largeIcon;
            if (small) *small = smallIcon;
            return S_OK;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
};

HRESULT ReadBounded(IStream* source,std::vector<BYTE>& bytes) {
    STATSTG stat{};
    if (SUCCEEDED(source->Stat(&stat,STATFLAG_NONAME)) && stat.cbSize.QuadPart > NookMaximumSvgBytes) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
    LARGE_INTEGER begin{}; source->Seek(begin,STREAM_SEEK_SET,nullptr);
    BYTE block[32768];
    while (true) {
        ULONG read = 0; HRESULT hr = source->Read(block,sizeof(block),&read);
        if (FAILED(hr)) return hr;
        if (read > sizeof(block) || bytes.size() + read > NookMaximumSvgBytes) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        bytes.insert(bytes.end(),block,block + read);
        if (hr == S_FALSE || !read) break;
    }
    return bytes.empty() ? InvalidData : S_OK;
}

bool SafeCss(const std::wstring& value) {
    // CSS escapes and at-rules could disguise external URLs/imports. A failed
    // safe subset falls back to a format icon; it is never sent to the renderer.
    if (value.find(L'\\') != std::wstring::npos || value.find(L'@') != std::wstring::npos ||
        value.find(L"/*") != std::wstring::npos || value.find(L"*/") != std::wstring::npos) return false;
    for (size_t i = 0; i + 2 < value.size(); ++i) {
        if ((value[i] != L'u' && value[i] != L'U') || (value[i+1] != L'r' && value[i+1] != L'R') || (value[i+2] != L'l' && value[i+2] != L'L')) continue;
        size_t p = i + 3; while (p < value.size() && Space(value[p])) ++p;
        if (p == value.size() || value[p] != L'(') continue;
        ++p; while (p < value.size() && Space(value[p])) ++p;
        wchar_t quote = 0;
        if (p < value.size() && (value[p] == L'\'' || value[p] == L'"')) quote = value[p++];
        if (p >= value.size() || value[p] != L'#') return false;
        ++p; bool identifier = false;
        for (; p < value.size(); ++p) {
            wchar_t ch = value[p];
            if ((quote && ch == quote) || (!quote && (ch == L')' || Space(ch)))) break;
            if (!(ch >= L'a' && ch <= L'z') && !(ch >= L'A' && ch <= L'Z') && !(ch >= L'0' && ch <= L'9') && ch != L'_' && ch != L'-' && ch != L'.' && ch != L':') return false;
            identifier = true;
        }
        if (!identifier || p >= value.size()) return false;
        if (quote) { if (value[p] != quote) return false; ++p; }
        while (p < value.size() && Space(value[p])) ++p;
        if (p >= value.size() || value[p] != L')') return false;
        i = p;
    }
    return true;
}

bool ForbiddenElement(const wchar_t* local) {
    const wchar_t* blocked[] = {L"script",L"foreignObject",L"iframe",L"object",L"embed",L"image",L"feImage",L"audio",L"video",L"animate",L"animateMotion",L"animateTransform",L"set"};
    for (auto name : blocked) if (_wcsicmp(local,name) == 0) return true;
    return false;
}

// Parse XML before resvg sees any bytes. XmlLite decodes entities in attribute
// values, so entity-encoded hrefs cannot evade the resource policy. Rewriting
// also removes processing instructions/comments and never emits a DTD.
HRESULT SanitizeSvg(const std::vector<BYTE>& input,std::vector<BYTE>& clean,bool& hasText) {
    hasText = false;
    ComPtr<IStream> inputStream(SHCreateMemStream(input.data(),static_cast<UINT>(input.size())));
    if (!inputStream.get()) return E_OUTOFMEMORY;
    ComPtr<IXmlReader> reader;
    HRESULT hr = CreateXmlReader(__uuidof(IXmlReader),reinterpret_cast<void**>(reader.put()),nullptr);
    if (FAILED(hr)) return hr;
    if (FAILED(hr = reader->SetProperty(XmlReaderProperty_DtdProcessing,DtdProcessing_Prohibit)) ||
        FAILED(hr = reader->SetProperty(XmlReaderProperty_MaxElementDepth,96)) ||
        FAILED(hr = reader->SetInput(inputStream.get()))) return hr;
    ComPtr<IStream> output;
    if (FAILED(hr = CreateStreamOnHGlobal(nullptr,TRUE,output.put()))) return hr;
    ComPtr<IXmlWriter> writer;
    if (FAILED(hr = CreateXmlWriter(__uuidof(IXmlWriter),reinterpret_cast<void**>(writer.put()),nullptr)) ||
        FAILED(hr = writer->SetProperty(XmlWriterProperty_OmitXmlDeclaration,TRUE)) ||
        FAILED(hr = writer->SetOutput(output.get()))) return hr;
    unsigned int nodes = 0;
    bool rootSeen = false, inStyle = false;
    std::wstring style;
    XmlNodeType type{};
    while ((hr = reader->Read(&type)) == S_OK) {
        if (++nodes > 100000) return InvalidData;
        const wchar_t *local = nullptr,*prefix = nullptr,*ns = nullptr,*value = nullptr;
        UINT length = 0;
        if (type == XmlNodeType_Element) {
            if (inStyle) return InvalidData;
            if (FAILED(reader->GetLocalName(&local,nullptr)) || FAILED(reader->GetPrefix(&prefix,nullptr)) || FAILED(reader->GetNamespaceUri(&ns,nullptr))) return InvalidData;
            if (!rootSeen) { if (!Same(local,L"svg") || (ns && *ns && !Same(ns,L"http://www.w3.org/2000/svg"))) return InvalidData; rootSeen = true; }
            if (ForbiddenElement(local)) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            if (ns && *ns && !Same(ns,L"http://www.w3.org/2000/svg")) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            if (Same(local,L"text")) hasText = true;
            const bool isStyle = Same(local,L"style");
            const BOOL empty = reader->IsEmptyElement();
            if (FAILED(hr = writer->WriteStartElement(prefix,local,ns))) return hr;
            unsigned int attributes = 0;
            HRESULT attr = reader->MoveToFirstAttribute();
            while (attr == S_OK) {
                if (++attributes > 256 || FAILED(reader->GetLocalName(&local,nullptr)) || FAILED(reader->GetPrefix(&prefix,nullptr)) || FAILED(reader->GetNamespaceUri(&ns,nullptr)) || FAILED(reader->GetValue(&value,&length))) return InvalidData;
                std::wstring attribute(value,length), name(local);
                if (StartsAscii(name,L"on") || EqualsAscii(name,L"src") || (EqualsAscii(name,L"base") && Same(prefix,L"xml"))) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                if (EqualsAscii(name,L"href")) {
                    size_t at = attribute.find_first_not_of(L" \t\r\n");
                    if (at == std::wstring::npos || attribute[at] != L'#') return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                }
                if (!Same(prefix,L"xmlns") && !Same(local,L"xmlns") && !SafeCss(attribute)) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                if (FAILED(hr = writer->WriteAttributeString(prefix,local,ns,attribute.c_str()))) return hr;
                attr = reader->MoveToNextAttribute();
            }
            if (FAILED(attr)) return attr;
            reader->MoveToElement();
            if (empty) { if (FAILED(hr = writer->WriteEndElement())) return hr; }
            else if (isStyle) { inStyle = true; style.clear(); }
        } else if (type == XmlNodeType_EndElement) {
            if (inStyle) {
                if (FAILED(reader->GetLocalName(&local,nullptr)) || !Same(local,L"style") || !SafeCss(style)) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                if (FAILED(hr = writer->WriteString(style.c_str()))) return hr;
                inStyle = false; style.clear();
            }
            if (FAILED(hr = writer->WriteFullEndElement())) return hr;
        } else if (type == XmlNodeType_Text || type == XmlNodeType_CDATA || type == XmlNodeType_Whitespace) {
            if (FAILED(reader->GetValue(&value,&length))) return InvalidData;
            if (inStyle) {
                if (style.size() + length > NookMaximumSvgBytes) return InvalidData;
                style.append(value,length);
            } else if (FAILED(hr = type == XmlNodeType_Whitespace ? writer->WriteWhitespace(value) : writer->WriteString(value))) return hr;
        } else if (type == XmlNodeType_DocumentType) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        // No processing instructions, XML declarations or comments are copied.
    }
    if (hr != S_FALSE || !rootSeen || inStyle) return FAILED(hr) ? hr : InvalidData;
    if (FAILED(hr = writer->Flush())) return hr;
    STATSTG stat{};
    if (FAILED(hr = output->Stat(&stat,STATFLAG_NONAME))) return hr;
    if (!stat.cbSize.QuadPart || stat.cbSize.QuadPart > 2UL * NookMaximumSvgBytes) return InvalidData;
    clean.resize(static_cast<size_t>(stat.cbSize.QuadPart));
    LARGE_INTEGER start{}; ULONG actual = 0;
    if (FAILED(hr = output->Seek(start,STREAM_SEEK_SET,nullptr)) || FAILED(hr = output->Read(clean.data(),static_cast<ULONG>(clean.size()),&actual))) return hr;
    return actual == clean.size() ? S_OK : InvalidData;
}

class UserSecurity {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
public:
    std::wstring sid;
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES),nullptr,FALSE};
    ~UserSecurity() { if (descriptor) LocalFree(descriptor); }
    HRESULT Create() {
        HANDLE raw = nullptr; if (!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&raw)) return LastErrorResult();
        Handle token(raw); DWORD size = 0;
        GetTokenInformation(token.get(),TokenUser,nullptr,0,&size);
        if (size < sizeof(TOKEN_USER) || size > 65536) return E_FAIL;
        std::vector<BYTE> bytes(size);
        if (!GetTokenInformation(token.get(),TokenUser,bytes.data(),size,&size)) return LastErrorResult();
        LPWSTR text = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid,&text)) return LastErrorResult();
        sid = text; LocalFree(text);
        const auto sddl = L"D:P(A;;GA;;;" + sid + L")S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr)) return LastErrorResult();
        attributes.lpSecurityDescriptor = descriptor;
        return S_OK;
    }
};

class SemaphoreSlot {
    Handle semaphore;
    bool acquired = false;
public:
    ~SemaphoreSlot() { if (acquired) ReleaseSemaphore(semaphore.get(),1,nullptr); }
    HRESULT Acquire(UserSecurity& security) {
        auto name = L"Local\\Nook.SvgThumbnail.Workers.v1." + security.sid;
        semaphore.reset(CreateSemaphoreW(&security.attributes,2,2,name.c_str()));
        if (!semaphore.valid()) return LastErrorResult();
        acquired = WaitForSingleObject(semaphore.get(),100) == WAIT_OBJECT_0;
        return acquired ? S_OK : HRESULT_FROM_WIN32(ERROR_BUSY);
    }
};

// Named pipes are local, private to the current user and asynchronously pumped
// on this COM call's thread. No helper thread can outlive the DLL or a timeout.
struct AsyncPipe {
    Handle server, child, event;
    // The overlapped read buffer must stay alive until this destructor has
    // cancelled and settled pending I/O, including during exception unwinding.
    std::array<BYTE,65536> buffer;
    OVERLAPPED operation{};
    bool pending = false;
    ~AsyncPipe() {
        if (pending && server.valid()) {
            CancelIoEx(server.get(),&operation);
            DWORD ignored = 0; GetOverlappedResult(server.get(),&operation,&ignored,TRUE);
        }
    }
    HRESULT Create(const std::wstring& name,bool childReads,UserSecurity& security) {
        event.reset(CreateEventW(nullptr,TRUE,FALSE,nullptr));
        if (!event.valid()) return LastErrorResult();
        operation.hEvent = event.get();
        DWORD access = (childReads ? PIPE_ACCESS_OUTBOUND : PIPE_ACCESS_INBOUND) | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE;
        server.reset(CreateNamedPipeW(name.c_str(),access,PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,0,&security.attributes));
        if (!server.valid()) return LastErrorResult();
        if (!ConnectNamedPipe(server.get(),&operation)) {
            const DWORD error = GetLastError();
            if (error == ERROR_IO_PENDING) pending = true;
            else if (error != ERROR_PIPE_CONNECTED) return HRESULT_FROM_WIN32(error);
        }
        SECURITY_ATTRIBUTES inherited = security.attributes; inherited.bInheritHandle = TRUE;
        child.reset(CreateFileW(name.c_str(),childReads ? GENERIC_READ : GENERIC_WRITE,0,&inherited,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
        if (!child.valid()) return LastErrorResult();
        if (pending) {
            if (WaitForSingleObject(event.get(),1000) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            DWORD connected = 0;
            if (!GetOverlappedResult(server.get(),&operation,&connected,FALSE)) return LastErrorResult();
            pending = false;
        }
        return S_OK;
    }
    void Begin() { ResetEvent(event.get()); operation.Internal = 0; operation.InternalHigh = 0; operation.Offset = 0; operation.OffsetHigh = 0; }
};

HRESULT RenderSvg(const std::vector<BYTE>& clean,UINT side,bool hasText,std::vector<BYTE>& png) {
    const std::wstring renderer = ModuleDirectory() + L"\\resvg.exe";
    if (!OrdinaryFile(renderer)) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    // Hold the exact renderer against modification during launch/use. It is a
    // pinned, application-owned sibling, never searched on PATH or via a shell.
    Handle rendererLock(CreateFileW(renderer.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    if (!rendererLock.valid()) return LastErrorResult();
    UserSecurity security; HRESULT hr = security.Create(); if (FAILED(hr)) return hr;
    SemaphoreSlot slot; if (FAILED(hr = slot.Acquire(security))) return hr;
    GUID identifier{}; if (FAILED(hr = CoCreateGuid(&identifier))) return hr;
    wchar_t guid[40]{}; StringFromGUID2(identifier,guid,40);
    const std::wstring pipeName = L"\\\\.\\pipe\\Nook.SvgThumbnail." + std::to_wstring(GetCurrentProcessId()) + L"." + guid;
    AsyncPipe input,output;
    if (FAILED(hr = input.Create(pipeName + L".in",true,security)) || FAILED(hr = output.Create(pipeName + L".out",false,security))) return hr;
    SECURITY_ATTRIBUTES inherit{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
    Handle errors(CreateFileW(L"NUL",GENERIC_WRITE,FILE_SHARE_READ | FILE_SHARE_WRITE,&inherit,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    if (!errors.valid()) return LastErrorResult();
    Handle job(CreateJobObjectW(nullptr,nullptr));
    if (!job.valid()) return LastErrorResult();
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    limits.BasicLimitInformation.ActiveProcessLimit = 1;
    limits.ProcessMemoryLimit = 256ULL * 1024 * 1024;
    if (!SetInformationJobObject(job.get(),JobObjectExtendedLimitInformation,&limits,sizeof(limits))) return LastErrorResult();
    SIZE_T attributeBytes = 0;
    InitializeProcThreadAttributeList(nullptr,1,0,&attributeBytes);
    std::vector<BYTE> attributes(attributeBytes);
    auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
    if (!InitializeProcThreadAttributeList(list,1,0,&attributeBytes)) return LastErrorResult();
    struct DeleteAttributes { LPPROC_THREAD_ATTRIBUTE_LIST list; ~DeleteAttributes(){DeleteProcThreadAttributeList(list);} } deleteAttributes{list};
    HANDLE inherited[] = {input.child.get(),output.child.get(),errors.get()};
    if (!UpdateProcThreadAttribute(list,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr)) return LastErrorResult();
    STARTUPINFOEXW start{}; start.StartupInfo.cb = sizeof(start); start.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    start.StartupInfo.hStdInput = input.child.get(); start.StartupInfo.hStdOutput = output.child.get(); start.StartupInfo.hStdError = errors.get(); start.lpAttributeList = list;
    std::wstring command = L"\"" + renderer + L"\" --width " + std::to_wstring(side) + L" --height " + std::to_wstring(side) + L" - -c";
    std::vector<wchar_t> writable(command.begin(),command.end()); writable.push_back(0);
    PROCESS_INFORMATION info{};
    if (!CreateProcessW(renderer.c_str(),writable.data(),nullptr,nullptr,TRUE,CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,nullptr,ModuleDirectory().c_str(),&start.StartupInfo,&info)) return LastErrorResult();
    Handle process(info.hProcess),thread(info.hThread);
    if (!AssignProcessToJobObject(job.get(),process.get())) { hr = LastErrorResult(); TerminateProcess(process.get(),1); WaitForSingleObject(process.get(),1000); return hr; }
    if (ResumeThread(thread.get()) == static_cast<DWORD>(-1)) { hr = LastErrorResult(); TerminateJobObject(job.get(),1); return hr; }
    input.child.reset(); output.child.reset(); errors.reset(); thread.reset();
    const ULONGLONG deadline = GetTickCount64() + (hasText ? NookTextRendererTimeoutMs : NookRendererTimeoutMs);
    size_t sent = 0; DWORD writeLength = 0; bool eof = false;
    hr = S_OK;
    while (true) {
        if (GetTickCount64() >= deadline) { hr = HRESULT_FROM_WIN32(ERROR_TIMEOUT); break; }
        if (!input.pending && input.server.valid()) {
            if (sent == clean.size()) input.server.reset();
            else {
                input.Begin(); writeLength = static_cast<DWORD>(std::min<size_t>(65536,clean.size() - sent)); DWORD written = 0;
                if (WriteFile(input.server.get(),clean.data() + sent,writeLength,&written,&input.operation)) sent += written;
                else if (GetLastError() == ERROR_IO_PENDING) input.pending = true;
                else { hr = LastErrorResult(); break; }
            }
        }
        if (!output.pending && !eof) {
            output.Begin(); DWORD received = 0;
            if (ReadFile(output.server.get(),output.buffer.data(),static_cast<DWORD>(output.buffer.size()),&received,&output.operation)) {
                if (png.size() + received > NookMaximumPngBytes) { hr = HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE); break; }
                png.insert(png.end(),output.buffer.data(),output.buffer.data() + received);
            } else if (GetLastError() == ERROR_IO_PENDING) output.pending = true;
            else if (GetLastError() == ERROR_BROKEN_PIPE) eof = true;
            else { hr = LastErrorResult(); break; }
        }
        if (input.pending && WaitForSingleObject(input.event.get(),0) == WAIT_OBJECT_0) {
            DWORD written = 0; input.pending = false;
            if (!GetOverlappedResult(input.server.get(),&input.operation,&written,FALSE) || written != writeLength) { hr = InvalidData; break; }
            sent += written;
        }
        if (output.pending && WaitForSingleObject(output.event.get(),0) == WAIT_OBJECT_0) {
            DWORD received = 0; output.pending = false;
            if (!GetOverlappedResult(output.server.get(),&output.operation,&received,FALSE)) {
                if (GetLastError() == ERROR_BROKEN_PIPE) eof = true;
                else { hr = LastErrorResult(); break; }
            } else {
                if (png.size() + received > NookMaximumPngBytes) { hr = HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE); break; }
                png.insert(png.end(),output.buffer.data(),output.buffer.data() + received);
            }
        }
        const bool exited = WaitForSingleObject(process.get(),0) == WAIT_OBJECT_0;
        if (exited && eof) {
            DWORD code = 1;
            if (!GetExitCodeProcess(process.get(),&code) || code || sent != clean.size() || png.empty()) hr = InvalidData;
            break;
        }
        HANDLE waits[3]; DWORD count = 0;
        if (input.pending) waits[count++] = input.event.get();
        if (output.pending) waits[count++] = output.event.get();
        if (!exited) waits[count++] = process.get();
        if (count) WaitForMultipleObjects(count,waits,FALSE,10);
    }
    if (FAILED(hr) || WaitForSingleObject(process.get(),0) != WAIT_OBJECT_0) {
        TerminateJobObject(job.get(),1); WaitForSingleObject(process.get(),1000);
    }
    // Cancelled overlapped operations are settled by AsyncPipe destructors;
    // process/job teardown happens before their buffers leave this stack.
    return hr;
}

HRESULT DecodePng(const std::vector<BYTE>& png,UINT side,HBITMAP* result) {
    ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,__uuidof(IWICImagingFactory),reinterpret_cast<void**>(factory.put()));
    if (FAILED(hr)) return hr;
    ComPtr<IWICStream> stream;
    if (FAILED(hr = factory->CreateStream(stream.put())) || FAILED(hr = stream->InitializeFromMemory(const_cast<BYTE*>(png.data()),static_cast<DWORD>(png.size())))) return hr;
    ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(hr = factory->CreateDecoderFromStream(stream.get(),nullptr,WICDecodeMetadataCacheOnLoad,decoder.put()))) return hr;
    GUID format{};
    if (FAILED(decoder->GetContainerFormat(&format)) || !IsEqualGUID(format,GUID_ContainerFormatPng)) return InvalidData;
    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(hr = decoder->GetFrame(0,frame.put()))) return hr;
    UINT width = 0,height = 0;
    if (FAILED(hr = frame->GetSize(&width,&height))) return hr;
    if (!width || !height || width > side || height > side || width > NookMaximumThumbnailSide || height > NookMaximumThumbnailSide) return InvalidData;
    ComPtr<IWICFormatConverter> converter;
    if (FAILED(hr = factory->CreateFormatConverter(converter.put())) || FAILED(hr = converter->Initialize(frame.get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom))) return hr;
    BITMAPINFO bitmap{}; bitmap.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); bitmap.bmiHeader.biWidth = static_cast<LONG>(width); bitmap.bmiHeader.biHeight = -static_cast<LONG>(height); bitmap.bmiHeader.biPlanes = 1; bitmap.bmiHeader.biBitCount = 32; bitmap.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr; HBITMAP image = CreateDIBSection(nullptr,&bitmap,DIB_RGB_COLORS,&pixels,nullptr,0);
    if (!image || !pixels) { if (image) DeleteObject(image); return E_OUTOFMEMORY; }
    hr = converter->CopyPixels(nullptr,width * 4,width * height * 4,static_cast<BYTE*>(pixels));
    if (FAILED(hr)) { DeleteObject(image); return hr; }
    *result = image; return S_OK;
}

class SvgThumbnail final : public IInitializeWithStream, public IThumbnailProvider {
    std::atomic<ULONG> references{1};
    ComPtr<IStream> stream;
public:
    SvgThumbnail() { ++objects; }
    ~SvgThumbnail() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        if (IsEqualIID(id,IID_IUnknown) || IsEqualIID(id,__uuidof(IInitializeWithStream))) *result = static_cast<IInitializeWithStream*>(this);
        else if (IsEqualIID(id,__uuidof(IThumbnailProvider))) *result = static_cast<IThumbnailProvider*>(this);
        if (!*result) return E_NOINTERFACE; AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --references; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE Initialize(IStream* input,DWORD mode) override {
        if (!input) return E_POINTER;
        if (stream.get()) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        if ((mode & 3) != STGM_READ) return STG_E_ACCESSDENIED;
        input->AddRef(); stream.reset(input); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetThumbnail(UINT side,HBITMAP* image,WTS_ALPHATYPE* alpha) override {
        if (image) *image = nullptr;
        if (alpha) *alpha = WTSAT_UNKNOWN;
        if (!image || !alpha) return E_POINTER;
        try {
            if (!stream.get()) return E_UNEXPECTED;
            if (!side || side > NookMaximumThumbnailSide) return E_INVALIDARG;
            std::vector<BYTE> input,clean,png;
            bool hasText = false;
            HRESULT hr = ReadBounded(stream.get(),input);
            if (FAILED(hr) || FAILED(hr = SanitizeSvg(input,clean,hasText)) || FAILED(hr = RenderSvg(clean,side,hasText,png)) || FAILED(hr = DecodePng(png,side,image))) return hr;
            *alpha = WTSAT_ARGB; return S_OK;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
};

class Factory final : public IClassFactory {
    std::atomic<ULONG> references{1};
    bool thumbnail;
public:
    explicit Factory(bool value) : thumbnail(value) { ++objects; }
    ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        if (!IsEqualIID(id,IID_IUnknown) && !IsEqualIID(id,IID_IClassFactory)) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG n = --references; if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID id,void** result) override {
        if (!result) return E_POINTER; *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try {
            IUnknown* instance = nullptr;
            if (thumbnail) instance = static_cast<IInitializeWithStream*>(new SvgThumbnail());
            else instance = static_cast<IExtractIconW*>(new FileIcon());
            HRESULT hr = instance->QueryInterface(id,result); instance->Release(); return hr;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++objects; else --objects; return S_OK; }
};
} // namespace

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid,REFIID id,void** result) {
    if (!result) return E_POINTER; *result = nullptr;
    try {
        if (!IsEqualCLSID(clsid,CLSID_NookFileIcon) && !IsEqualCLSID(clsid,CLSID_NookSvgThumbnail)) return CLASS_E_CLASSNOTAVAILABLE;
        auto factory = new Factory(IsEqualCLSID(clsid,CLSID_NookSvgThumbnail));
        HRESULT hr = factory->QueryInterface(id,result); factory->Release(); return hr;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects.load() == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { moduleHandle = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
