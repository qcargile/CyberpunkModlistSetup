#include "Catalog.h"

#include "Detect.h"
#include "Net.h"
#include "Run.h"
#include "SystemTweaks.h"
#include "Verify.h"

#include "CleanCore.h"
#include "GameDetect.h"
#include "Quarantine.h"
#include "Util.h"

#include <simdjson.h>

#include <filesystem>
#include <fstream>
#include <iterator>
#include <map>

namespace fs = std::filesystem;
using cleanslate::Widen;
using cleanslate::NarrowU8;
using cleanslate::NowStamp;

namespace cbsetup {

namespace {

const wchar_t* kVcX64    = L"https://aka.ms/vs/17/release/vc_redist.x64.exe";
const wchar_t* kVcX86    = L"https://aka.ms/vs/17/release/vc_redist.x86.exe";
const wchar_t* kDotNet   = L"https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
const wchar_t* kDotNetPg = L"https://dotnet.microsoft.com/download/dotnet/8.0";
const wchar_t* kDxRedist = L"https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe";
const wchar_t* kDxPage   = L"https://www.microsoft.com/download/details.aspx?id=35";
const wchar_t* kVortexPg = L"https://www.nexusmods.com/site/mods/1";
const wchar_t* kWabbajack = L"https://github.com/wabbajack-tools/wabbajack/releases/latest/download/Wabbajack.exe";
const char*    kWjGallery = "https://www.wabbajack.org/gallery?game=cyberpunk2077&nsfw=indeterminate";
const char*    kVxCollections = "https://www.nexusmods.com/games/cyberpunk2077/collections?sort=downloads";

std::wstring InstallW(const Model& m)   { return Widen(std::string(m.installPath)); }
std::wstring WjW(const Model& m)        { return Widen(std::string(m.wjPath)); }
std::wstring CheckRootW(const Model& m) { return (m.mode == Mode::MO2) ? WjW(m) : m.gameDir; }

std::wstring ToolCacheDir() {
    wchar_t buf[1024];
    std::wstring dir;
    if (::ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\CyberpunkModlistSetup\\cache", buf, 1024) > 0 && buf[0] != L'%')
        dir = buf;
    else if (::ExpandEnvironmentStringsW(L"%TEMP%\\CyberpunkModlistSetup", buf, 1024) > 0)
        dir = buf;
    if (!dir.empty()) { std::error_code ec; fs::create_directories(fs::path(dir), ec); }
    return dir;
}

std::string GbStr(uint64_t bytes) {
    double gb = (double)bytes / (1024.0 * 1024.0 * 1024.0);
    char b[64];
    std::snprintf(b, sizeof(b), "%.0f GB", gb);
    return b;
}

bool InstallerSucceeded(int code) {
    return code == 0 || code == 3010 || code == 1638;
}

bool VortexInRegistry(std::wstring* outExe = nullptr) {
    struct Hive { HKEY root; const wchar_t* sub; };
    const Hive hives[] = {
        { HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" },
        { HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall" },
        { HKEY_CURRENT_USER,  L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" },
    };
    for (const auto& hv : hives) {
        HKEY h;
        if (::RegOpenKeyExW(hv.root, hv.sub, 0, KEY_READ, &h) != ERROR_SUCCESS) continue;
        wchar_t key[256];
        DWORD idx = 0, klen = 256;
        bool hit = false;
        while (!hit && ::RegEnumKeyExW(h, idx, key, &klen, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
            std::wstring path = std::wstring(hv.sub) + L"\\" + key;
            wchar_t dn[128] = {};
            DWORD dnsz = sizeof(dn);
            if (::RegGetValueW(hv.root, path.c_str(), L"DisplayName", RRF_RT_REG_SZ, nullptr, dn, &dnsz) == ERROR_SUCCESS
                && ::lstrcmpiW(dn, L"Vortex") == 0) {
                std::error_code ec;
                wchar_t icon[1024] = {};
                DWORD isz = sizeof(icon);
                if (::RegGetValueW(hv.root, path.c_str(), L"DisplayIcon", RRF_RT_REG_SZ, nullptr, icon, &isz) == ERROR_SUCCESS) {
                    std::wstring p = icon;
                    size_t comma = p.find_last_of(L',');
                    if (comma != std::wstring::npos) p.erase(comma);
                    if (!p.empty() && fs::exists(fs::path(p), ec)) { if (outExe) *outExe = p; hit = true; }
                }
                if (!hit) {
                    wchar_t loc[1024] = {};
                    DWORD lsz = sizeof(loc);
                    if (::RegGetValueW(hv.root, path.c_str(), L"InstallLocation", RRF_RT_REG_SZ, nullptr, loc, &lsz) == ERROR_SUCCESS
                        && loc[0] && fs::exists(fs::path(loc) / L"Vortex.exe", ec)) {
                        if (outExe) *outExe = (fs::path(loc) / L"Vortex.exe").wstring();
                        hit = true;
                    }
                }
            }
            ++idx;
            klen = 256;
        }
        ::RegCloseKey(h);
        if (hit) return true;
    }
    return false;
}

bool VortexInstalled(std::wstring* outExe = nullptr) {
    const wchar_t* cands[] = {
        L"%LOCALAPPDATA%\\Programs\\Vortex\\Vortex.exe",
        L"%ProgramFiles%\\Black Tree Gaming Ltd\\Vortex\\Vortex.exe",
    };
    wchar_t buf[1024];
    for (const wchar_t* c : cands) {
        if (::ExpandEnvironmentStringsW(c, buf, 1024)) {
            std::error_code ec;
            if (fs::exists(fs::path(buf), ec)) { if (outExe) *outExe = buf; return true; }
        }
    }
    if (VortexInRegistry(outExe)) return true;
    return IsProcessRunning(L"Vortex.exe");
}

std::wstring ArgvQuote(const std::wstring& arg) {
    if (!arg.empty() && arg.find_first_of(L" \t\n\v\"") == std::wstring::npos) return arg;
    std::wstring s = L"\"";
    for (auto it = arg.begin();; ++it) {
        unsigned slashes = 0;
        while (it != arg.end() && *it == L'\\') { ++it; ++slashes; }
        if (it == arg.end()) { s.append(slashes * 2, L'\\'); break; }
        if (*it == L'"') { s.append(slashes * 2 + 1, L'\\'); s.push_back(L'"'); }
        else { s.append(slashes, L'\\'); s.push_back(*it); }
    }
    s.push_back(L'"');
    return s;
}

bool WabbajackExeFromUri(std::wstring& out) {
    struct Cmd { HKEY root; const wchar_t* sub; };
    const Cmd cmds[] = {
        { HKEY_CURRENT_USER,  L"Software\\Classes\\wabbajack\\shell\\open\\command" },
        { HKEY_LOCAL_MACHINE, L"SOFTWARE\\Classes\\wabbajack\\shell\\open\\command" },
        { HKEY_CLASSES_ROOT,  L"wabbajack\\shell\\open\\command" },
    };
    for (const auto& c : cmds) {
        wchar_t buf[1024] = {};
        DWORD sz = sizeof(buf);
        if (::RegGetValueW(c.root, c.sub, nullptr, RRF_RT_REG_SZ, nullptr, buf, &sz) != ERROR_SUCCESS || !buf[0]) continue;
        std::wstring cmd = buf, exe;
        if (cmd[0] == L'"') {
            size_t q = cmd.find(L'"', 1);
            if (q != std::wstring::npos) exe = cmd.substr(1, q - 1);
        } else {
            size_t sp = cmd.find(L' ');
            exe = (sp == std::wstring::npos) ? cmd : cmd.substr(0, sp);
        }
        std::error_code ec;
        if (!exe.empty() && fs::exists(fs::path(exe), ec)) { out = exe; return true; }
    }
    return false;
}

bool WabbajackInstalled(const std::wstring& installDir, std::wstring* outExe = nullptr) {
    std::error_code ec;
    if (!installDir.empty()) {
        std::wstring inExe = (fs::path(installDir) / L"Wabbajack.exe").wstring();
        if (fs::exists(fs::path(inExe), ec)) { if (outExe) *outExe = inExe; return true; }
    }
    std::wstring uri;
    if (WabbajackExeFromUri(uri)) { if (outExe) *outExe = uri; return true; }
    if (IsProcessRunning(L"Wabbajack.exe")) { if (outExe) outExe->clear(); return true; }
    return false;
}

std::string PrettyDeployMethod(const std::string& method) {
    if (method == "hardlink_activator") return "Hardlink";
    if (method == "symlink_activator")  return "Symlink";
    if (method == "move_activator")     return "Move";
    if (method.empty())                 return "unset";
    return method;
}

bool SameDrive(const std::string& a, const std::wstring& b) {
    if (a.size() < 2 || a[1] != ':' || b.empty()) return true;
    char ca = a[0];    if (ca >= 'a' && ca <= 'z')   ca = (char)(ca - 32);
    wchar_t cb = b[0]; if (cb >= L'a' && cb <= L'z') cb = (wchar_t)(cb - 32);
    return (wchar_t)ca == cb;
}

std::string CollectionSlug(const std::string& url) {
    if (url.empty()) return "";
    std::string s = url;
    size_t q = s.find_first_of("?#");
    if (q != std::string::npos) s = s.substr(0, q);
    while (!s.empty() && (s.back() == '/' || s.back() == ' ')) s.pop_back();
    size_t slash = s.find_last_of('/');
    return (slash == std::string::npos) ? s : s.substr(slash + 1);
}

std::string QueryThirdPartyAv() {
    std::wstring cmd =
        L"$a = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue; "
        L"$t = $a | Where-Object { $_.displayName -notmatch 'Windows Defender|Microsoft Defender' -and ($_.productState -band 0x1000) }; "
        L"if ($t) { 'CBAV ' + (($t.displayName) -join ', ') } else { 'CBNONE' }";
    std::string out, err;
    RunPowerShellCapture(cmd, out, err);
    size_t k = out.find("CBAV ");
    if (k == std::string::npos) return "";
    std::string name = out.substr(k + 5);
    while (!name.empty() && (name.back() == '\n' || name.back() == '\r' || name.back() == ' ' || name.back() == '\t')) name.pop_back();
    return name;
}

std::string GpuVendorOf(const std::string& name) {
    std::string n = name;
    for (auto& c : n) c = (char)::tolower((unsigned char)c);
    if (n.find("nvidia") != std::string::npos) return "nvidia";
    if (n.find("amd") != std::string::npos || n.find("radeon") != std::string::npos) return "amd";
    return "";
}

std::string ResolveCollectionRevision(const std::string& slug) {
    if (slug.empty()) return "";
    std::string query = "{\"query\":\"{ collection(slug: \\\"" + slug +
        "\\\", domainName: \\\"cyberpunk2077\\\", viewAdultContent: true) "
        "{ latestPublishedRevision { revisionNumber } } }\"}";
    std::string resp, err;
    if (!HttpPostJson(L"https://api.nexusmods.com/v2/graphql", query, resp, err)) return "";
    try {
        simdjson::dom::parser parser;
        simdjson::dom::element root;
        if (parser.parse(simdjson::padded_string(resp)).get(root) != simdjson::SUCCESS) return "";
        int64_t rev = 0;
        auto rn = root["data"]["collection"]["latestPublishedRevision"]["revisionNumber"];
        if (rn.get(rev) == simdjson::SUCCESS && rev > 0) return std::to_string(rev);
    } catch (...) {
    }
    return "";
}

bool VerifyInstaller(Model& m, const std::wstring& path, const char* expectedSigner) {
    std::string reason;
    if (InstallerTrusted(path, expectedSigner, reason)) return true;
    Append(m, "  Refused to run " + NarrowU8(fs::path(path).filename().wstring()) + " - " + reason + ".");
    return false;
}

bool DownloadAndRun(Model& m, Step& s, const std::wstring& url,
                    const std::wstring& fileName, const std::wstring& runArgs,
                    const char* expectedSigner) {
    std::wstring dest = (fs::path(ToolCacheDir()) / fileName).wstring();
    SetLabel(m, "Downloading " + NarrowU8(fileName));
    m.dlDone = 0;
    m.dlTotal = 0;
    ProgressFn prog = [&m](uint64_t done, uint64_t total) { m.dlDone = done; m.dlTotal = total; };
    std::string err;
    if (!HttpGetToFile(url, dest, prog, err, &m.cancelRequested)) {
        Append(m, "  FAILED to download " + NarrowU8(fileName) + ": " + err);
        s.status = Status::Failed;
        return false;
    }
    if (!VerifyInstaller(m, dest, expectedSigner)) { s.status = Status::Failed; return false; }
    SetLabel(m, "Installing " + s.title);
    Append(m, "  Installing " + s.title + "...");
    int code = RunProcess(dest, runArgs, err);
    if (!InstallerSucceeded(code)) {
        char b[96];
        std::snprintf(b, sizeof(b), "  FAILED (installer exit code %d)", code);
        Append(m, b);
        s.status = Status::Failed;
        return false;
    }
    Append(m, "  Done: " + s.title);
    s.status = Status::Done;
    return true;
}

void ApplyVcRedist(Model& m, Step& s) {
    std::wstring winget = WingetPath();
    if (!winget.empty()) {
        Append(m, "  Installing all Visual C++ Redistributables via winget...");
        const wchar_t* ids[] = {
            L"Microsoft.VCRedist.2015+.x64", L"Microsoft.VCRedist.2015+.x86",
            L"Microsoft.VCRedist.2013.x64",  L"Microsoft.VCRedist.2013.x86",
            L"Microsoft.VCRedist.2012.x64",  L"Microsoft.VCRedist.2012.x86",
            L"Microsoft.VCRedist.2010.x64",  L"Microsoft.VCRedist.2010.x86",
            L"Microsoft.VCRedist.2008.x64",  L"Microsoft.VCRedist.2008.x86",
            L"Microsoft.VCRedist.2005.x64",  L"Microsoft.VCRedist.2005.x86",
        };
        int okCount = 0;
        int total = (int)(sizeof(ids) / sizeof(ids[0]));
        for (const wchar_t* id : ids) {
            SetLabel(m, "Installing " + NarrowU8(std::wstring(id)));
            std::wstring args = std::wstring(L"install --id ") + id +
                L" --exact --silent --accept-package-agreements --accept-source-agreements";
            std::string err;
            int code = RunProcess(winget, args, err);
            if (InstallerSucceeded(code)) ++okCount;
        }
        Append(m, "  Visual C++: " + std::to_string(okCount) + "/" + std::to_string(total) + " runtimes installed or already present.");
        s.status = (okCount > 0) ? Status::Done : Status::Failed;
        return;
    }
    Append(m, "  winget not available - installing the core 2015-2022 runtimes directly...");
    bool a = DownloadAndRun(m, s, kVcX64, L"vc_redist.x64.exe", L"/install /quiet /norestart", "Microsoft Corporation");
    Step tmp = s;
    bool b = DownloadAndRun(m, tmp, kVcX86, L"vc_redist.x86.exe", L"/install /quiet /norestart", "Microsoft Corporation");
    s.status = (a && b) ? Status::Done : Status::Failed;
}

void ApplyDotNet(Model& m, Step& s) {
    std::wstring winget = WingetPath();
    if (!winget.empty()) {
        SetLabel(m, "Installing .NET 8 Desktop Runtime (winget)");
        Append(m, "  Installing .NET 8 Desktop Runtime via winget...");
        std::string err;
        std::wstring args = L"install --id Microsoft.DotNet.DesktopRuntime.8 --architecture x64 "
                            L"--silent --accept-package-agreements --accept-source-agreements";
        int code = RunProcess(winget, args, err);
        if (InstallerSucceeded(code)) { Append(m, "  Done: .NET 8 Desktop Runtime"); s.status = Status::Done; return; }
        Append(m, "  winget path failed, falling back to direct download...");
    }
    DownloadAndRun(m, s, kDotNet, L"windowsdesktop-runtime-8-win-x64.exe", L"/install /quiet /norestart", "Microsoft Corporation");
}

void ApplyDirectX(Model& m, Step& s) {
    std::wstring redist = (fs::path(ToolCacheDir()) / L"directx_Jun2010_redist.exe").wstring();
    SetLabel(m, "Downloading DirectX runtime");
    m.dlDone = 0;
    m.dlTotal = 0;
    ProgressFn prog = [&m](uint64_t done, uint64_t total) { m.dlDone = done; m.dlTotal = total; };
    std::string err;
    if (!HttpGetToFile(kDxRedist, redist, prog, err, &m.cancelRequested)) {
        Append(m, "  FAILED to download DirectX runtime: " + err);
        s.status = Status::Failed;
        return;
    }
    if (!VerifyInstaller(m, redist, "Microsoft Corporation"))
        Append(m, "  (proceeding - this is the official Microsoft DirectX package over HTTPS; its 2010-era signature may not validate cleanly.)");
    std::wstring extractDir = (fs::path(ToolCacheDir()) / L"dxredist").wstring();
    std::error_code ec;
    fs::create_directories(fs::path(extractDir), ec);
    SetLabel(m, "Extracting DirectX runtime");
    Append(m, "  Extracting DirectX runtime...");
    int ex = RunProcess(redist, L"/Q /C /T:\"" + extractDir + L"\"", err);
    if (ex != 0) {
        Append(m, "  FAILED to extract DirectX runtime");
        s.status = Status::Failed;
        return;
    }
    std::wstring setup = (fs::path(extractDir) / L"DXSETUP.exe").wstring();
    if (!VerifyInstaller(m, setup, "Microsoft Corporation"))
        Append(m, "  (proceeding - DXSETUP is the official Microsoft DirectX installer.)");
    SetLabel(m, "Installing DirectX runtime");
    Append(m, "  Installing DirectX runtime...");
    int code = RunProcess(setup, L"/silent", err);
    if (!InstallerSucceeded(code)) {
        Append(m, "  FAILED installing DirectX runtime");
        s.status = Status::Failed;
        return;
    }
    Append(m, "  Done: DirectX End-User Runtime");
    s.status = Status::Done;
}

void ApplyTweak(Model& m, Step& s, bool (*fn)(std::string&), const char* working) {
    SetLabel(m, working);
    Append(m, std::string(working) + "...");
    std::string msg;
    bool ok = fn(msg);
    Append(m, "  " + msg);
    s.status = ok ? Status::Done : Status::Warning;
}

}

const char* WabbajackGalleryUrl() { return kWjGallery; }

const char* VortexCollectionsUrl() { return kVxCollections; }

bool StepVisible(const Model& m, const Step& s) {
    if (s.id == "phantomliberty" && !m.config.requiresPhantomLiberty) return false;
    if (s.id == "redmod" && !m.config.requiresRedmod) return false;
    if (s.applies == Applies::Shared) return true;
    if (m.mode == Mode::MO2 && s.applies == Applies::MO2) return true;
    if (m.mode == Mode::Vortex && s.applies == Applies::Vortex) return true;
    return false;
}

Step* Find(Model& m, const char* id) {
    for (auto& s : m.steps) if (s.id == id) return &s;
    return nullptr;
}

void Append(Model& m, const std::string& line) {
    std::lock_guard<std::mutex> lk(m.logMtx);
    m.log += line;
    m.log += "\n";
    static std::wstring logPath = []() {
        wchar_t t[1024] = {};
        DWORD n = ::GetTempPathW(1024, t);
        return std::wstring(t, n) + L"CyberpunkModlistSetup.log";
    }();
    std::ofstream f(fs::path(logPath), std::ios::app | std::ios::binary);
    if (f) f << line << "\n";
}

void SetLabel(Model& m, const std::string& label) {
    std::lock_guard<std::mutex> lk(m.logMtx);
    m.activeLabel = label;
}

namespace {
std::wstring StateFilePath() {
    wchar_t buf[1024] = {};
    if (::ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\CyberpunkModlistSetup", buf, 1024) == 0) return L"";
    return (fs::path(buf) / L"state.json").wstring();
}
}

void LoadState(Model& m) {
    std::wstring path = StateFilePath();
    if (path.empty()) return;
    std::ifstream f(fs::path(path), std::ios::binary);
    if (!f) return;
    std::string buf((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (buf.empty()) return;
    try {
        simdjson::dom::parser p;
        simdjson::dom::element root;
        if (p.parse(simdjson::padded_string(buf)).get(root) != simdjson::SUCCESS) return;
        std::string_view sv;
        if (root["installPath"].get(sv) == simdjson::SUCCESS && !sv.empty()) {
            std::string s(sv); strncpy_s(m.installPath, s.c_str(), sizeof(m.installPath) - 1);
        }
        if (root["downloadsPath"].get(sv) == simdjson::SUCCESS && !sv.empty()) {
            std::string s(sv); strncpy_s(m.downloadsPath, s.c_str(), sizeof(m.downloadsPath) - 1);
        }
        if (root["wjPath"].get(sv) == simdjson::SUCCESS && !sv.empty()) {
            std::string s(sv); strncpy_s(m.wjPath, s.c_str(), sizeof(m.wjPath) - 1);
        }
        simdjson::dom::array arr;
        if (root["userDone"].get(arr) == simdjson::SUCCESS) {
            m.userDone.clear();
            for (auto e : arr) { std::string_view ev; if (e.get(ev) == simdjson::SUCCESS) m.userDone.push_back(std::string(ev)); }
        }
    } catch (...) {
    }
}

void SaveState(Model& m) {
    std::wstring path = StateFilePath();
    if (path.empty()) return;
    std::error_code ec;
    fs::create_directories(fs::path(path).parent_path(), ec);
    const char* list = (m.list == ListId::ChromeAndBlood) ? "cab" : (m.list == ListId::Wtnc) ? "wtnc" : "none";
    const char* mode = (m.mode == Mode::Vortex) ? "vortex" : (m.mode == Mode::MO2) ? "mo2" : "none";
    std::string j = "{\n  \"list\": \"";
    j += list;
    j += "\",\n  \"manager\": \"";
    j += mode;
    j += "\",\n  \"installPath\": ";
    cleanslate::JsonEsc(j, std::string(m.installPath));
    j += ",\n  \"downloadsPath\": ";
    cleanslate::JsonEsc(j, std::string(m.downloadsPath));
    j += ",\n  \"wjPath\": ";
    cleanslate::JsonEsc(j, std::string(m.wjPath));
    j += ",\n  \"userDone\": [";
    for (size_t i = 0; i < m.userDone.size(); ++i) { if (i) j += ", "; cleanslate::JsonEsc(j, m.userDone[i]); }
    j += "]";
    j += "\n}\n";
    std::ofstream f(fs::path(path), std::ios::binary | std::ios::trunc);
    if (f) f << j;
}

void SuggestPaths(Model& m) {
    m.platform = DetectGameInstall(m.gameDir);
    std::wstring drive = m.gameDir.empty() ? L"C:\\" : DriveRootOf(m.gameDir);
    if (drive.empty()) drive = L"C:\\";
    std::wstring inst = drive + L"ModlistInstall";
    std::wstring dl = drive + L"ModlistInstall\\downloads";
    std::wstring wj = drive + L"Wabbajack";
    strncpy_s(m.installPath, NarrowU8(inst).c_str(), sizeof(m.installPath) - 1);
    strncpy_s(m.downloadsPath, NarrowU8(dl).c_str(), sizeof(m.downloadsPath) - 1);
    strncpy_s(m.wjPath, NarrowU8(wj).c_str(), sizeof(m.wjPath) - 1);
}

void BuildCatalog(Model& m) {
    m.steps.clear();
    auto add = [&](const char* id, const char* title, const char* detail, Group g, bool autoFix,
                   const char* guide, const wchar_t* url, Applies applies = Applies::Shared) {
        Step s;
        s.id = id; s.title = title; s.detail = detail; s.group = g; s.autoFixable = autoFix;
        s.applies = applies;
        s.guide = guide ? guide : "";
        s.url = url ? NarrowU8(std::wstring(url)) : "";
        m.steps.push_back(std::move(s));
    };

    add("game", "Cyberpunk 2077 install", "Installed, on the build this list targets, on the release branch.",
        Group::Check, false, "Install Cyberpunk 2077 first.", nullptr);
    add("phantomliberty", "Phantom Liberty", "Some lists require the Phantom Liberty expansion.",
        Group::Check, false, "", nullptr);
    add("redmod", "REDmod installed", "Free Steam DLC most lists require.",
        Group::Check, false, "Open the REDmod page in Steam and install it.", L"steam://install/2060310");
    add("disk", "Free space on the install drive", "The list needs free space on the drive it installs to.",
        Group::Check, false, "", nullptr);
    add("sync", "Install folder not in cloud sync", "OneDrive/Dropbox locks files mid-install and breaks the mod manager.",
        Group::Check, false, "", nullptr);
    add("writeperm", "Install folder writable + outside Program Files", "Program Files forces admin-only writes and breaks the mod manager; the folder must be writable.",
        Group::Check, false, "", nullptr);
    add("vortex_ntfs", "Game drive is NTFS (hardlinks)", "Vortex hardlink deployment requires an NTFS drive.",
        Group::Check, false,
        "Your game drive must be NTFS. FAT/exFAT can't do hardlinks - reformat to NTFS.", nullptr, Applies::Vortex);

    add("vcredist", "Visual C++ Redistributables", "Runtime libraries the game and tools need (x64 + x86).",
        Group::Dependency, true,
        "Download and run the x64 Visual C++ Redistributable from Microsoft (and the x86 one too).",
        L"https://aka.ms/vs/17/release/vc_redist.x64.exe");
    add("dotnet", ".NET 8 Desktop Runtime", "What Wabbajack and many tools run on.",
        Group::Dependency, true, "Download the .NET 8 Desktop Runtime (x64) and install it.", kDotNetPg);
    add("directx", "DirectX End-User Runtime", "Legacy DirectX libraries some mods and overlays use.",
        Group::Dependency, true, "Download the DirectX End-User Runtime web installer and run it.", kDxPage);
    add("longpaths", "Windows long paths enabled", "Deep mod folders can exceed the legacy 260-character path limit and fail to extract.",
        Group::Dependency, true,
        "regedit > HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem > set LongPathsEnabled to 1 > reboot.", nullptr);

    add("steamupdate", "Steam auto-update to launch-only", "Stops Steam patching the game out from under the list. Closes Steam to apply.",
        Group::Tweak, true,
        "In Steam: right-click Cyberpunk 2077 - Properties - Updates - set to launch-only.", nullptr);

    add("clean", "Clean game folder to vanilla", "Moves leftover mod files + game-data leftovers to a reversible backup.",
        Group::CleanInstall, false,
        "Remove leftover mod files: archive\\pc\\mod, red4ext, r6\\scripts, r6\\tweaks, bin\\x64\\plugins, mods\\, and any dxgi/d3d11/winmm proxy dll in bin\\x64. "
        "Also clear %LOCALAPPDATA%\\REDEngine (crash dumps) and, for a full reset, %LOCALAPPDATA%\\CD Projekt Red (this resets in-game settings, NOT your saves). "
        "Then in Steam: right-click the game > Properties > Installed Files > Verify integrity (reacquires under 1 GB of official files).", nullptr);

    add("defender", "Windows Defender exclusions (optional)", "Stops Defender quarantining files mid-install. Opt-in - many setups don't need it.",
        Group::Manual, false,
        "In Windows Security > Virus & threat protection > Manage settings > Exclusions, add your install + downloads folders. If it won't let you, turn Tamper Protection off first.", nullptr);
    add("m_av", "Remove third-party antivirus", "BitDefender/Norton/Webroot break the mod manager's virtual file system.",
        Group::Manual, false, "Windows Defender with sensible browsing is enough.", nullptr);
    add("m_gpu", "Update GPU drivers", "Latest NVIDIA App or AMD Adrenalin drivers.",
        Group::Manual, false, "Update through the NVIDIA App or AMD Adrenalin.", L"https://www.nvidia.com/software/nvidia-app/");

    add("wabbajack", "Wabbajack", "Downloads the latest Wabbajack into your Wabbajack folder.",
        Group::Manager, true, "Download Wabbajack from wabbajack.org and put the exe in your Wabbajack folder.",
        L"https://www.wabbajack.org", Applies::MO2);
    add("wj_findlist", "Send the list to Wabbajack", "Opens Wabbajack on this list. Free Nexus accounts click each download; premium is hands-off.",
        Group::Manager, false,
        "In Wabbajack: pick the list, set your Install + Downloads folders, then Install.", nullptr, Applies::MO2);

    add("vortex", "Vortex", "Downloads and installs the latest Vortex.",
        Group::Manager, true, "Download Vortex from Nexus Mods and install it.", kVortexPg, Applies::Vortex);
    add("vortex_extension", "Vortex: manage Cyberpunk + install the extension",
        "First Vortex step. Managing the game installs its extension, Vortex restarts, and the game is managed.",
        Group::Manual, false,
        "In Vortex: Games - Cyberpunk 2077 - Manage. It prompts to download the game extension - install it and let Vortex restart. The game is now managed.", nullptr, Applies::Vortex);
    add("vortex_redmod", "Vortex: V2077 Settings (two toggles off)",
        "Under Cyberpunk's Preferences in Vortex. Both default to off, so usually just confirm.",
        Group::Manual, false,
        "Cyberpunk 2077 - Preferences - V2077 Settings: turn OFF 'Automatically convert legacy archive mods to REDmods' and 'Don't prompt when reaching the fallback installer'.", nullptr, Applies::Vortex);
    add("vortex_staging", "Vortex: Mod Staging Folder",
        "Point Vortex's staging folder at a folder OUTSIDE the Vortex app folder, on the same drive as the game. Read live from Vortex.",
        Group::Manual, false,
        "Preferences - Mods - set Mod Staging Folder to your step-1 Mod Staging folder, then Apply. It can't be inside the Vortex application folder.", nullptr, Applies::Vortex);
    add("vortex_hardlink", "Vortex: Deployment Method = Hardlink",
        "Set Vortex's Deployment Method to Hardlink. Read live from Vortex.",
        Group::Manual, false,
        "Preferences - Mods - Deployment Method - Hardlink Deployment, then Apply.", nullptr, Applies::Vortex);
    add("vortex_profile", "Vortex: create + enable a profile",
        "A clean profile for the collection, then enable it.",
        Group::Manual, false,
        "Profiles tab - Add Cyberpunk 2077 profile - give it a name, Save. Then hit ENABLE on the profile you just made so Vortex switches to it.", nullptr, Applies::Vortex);
    add("vortex_collection", "Vortex: add + install the collection", "Open Vortex straight to this collection (Add to Vortex), or Browse to find it.",
        Group::Manual, false,
        "Add to Vortex opens it ready to install (or use Browse). In Vortex: Install Now. When it finishes: Mods tab - Deploy Mods - Play.", nullptr, Applies::Vortex);

    if (!m.config.collectionUrl.empty()) {
        if (Step* s = Find(m, "vortex_collection")) s->url = m.config.collectionUrl;
    }
    if (!m.config.wabbajackUrl.empty()) {
        if (Step* s = Find(m, "wj_findlist")) s->url = m.config.wabbajackUrl;
    }
}

void DetectAll(Model& m) {
    if (m.gameDir.empty()) m.platform = DetectGameInstall(m.gameDir);
    else m.platform = ClassifyGameDir(m.gameDir);

    bool nonSteam = (m.platform == Platform::GOG || m.platform == Platform::Epic);

    if (!m.avChecked) { m.thirdPartyAv = QueryThirdPartyAv(); m.avChecked = true; }

    uint64_t requiredBytes = m.config.requiredGB * 1024ULL * 1024ULL * 1024ULL;

    if (Step* s = Find(m, "game")) {
        std::error_code ec;
        bool valid = !m.gameDir.empty() && fs::exists(fs::path(m.gameDir) / L"bin\\x64\\Cyberpunk2077.exe", ec);
        if (!valid) {
            s->status = Status::Missing;
            s->statusText = "not found - install Cyberpunk 2077, or set the game path below";
        } else {
            std::string plat = (m.platform == Platform::Unknown) ? "" : std::string(" (") + PlatformName(m.platform) + ")";
            SteamGameInfo gi = ReadSteamGameInfo(m.gameDir);
            const uint64_t kFullyInstalled = 4;
            const uint64_t kNotCleanMask = 2 | 32 | 128 | 256 | 512 | 1024 | 2048 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
            bool flagsKnown = gi.manifestFound && gi.stateFlags != 0;
            bool midUpdate = (flagsKnown && ((gi.stateFlags & kFullyInstalled) == 0 || (gi.stateFlags & kNotCleanMask) != 0))
                          || (!gi.buildId.empty() && !gi.targetBuildId.empty() && gi.buildId != gi.targetBuildId);
            std::string bkLower = gi.betaKey;
            if (!bkLower.empty()) ::CharLowerBuffA(bkLower.data(), (DWORD)bkLower.size());
            bool onBeta = !gi.betaKey.empty() && bkLower != "public";
            bool buildMismatch = !m.config.expectedBuildId.empty() && gi.manifestFound && !gi.buildId.empty()
                              && !midUpdate && m.config.expectedBuildId != gi.buildId;
            std::string verDisp = m.config.gameVersion.empty() ? "" : (" v" + m.config.gameVersion);

            if (buildMismatch) {
                s->status = Status::Warning;
                s->statusText = "installed, but on build " + gi.buildId + " - this list needs build " + m.config.expectedBuildId + verDisp
                    + ". Framework mods (RED4ext, CET) won't load until they match - update or pin the game build in Steam.";
            } else if (onBeta) {
                s->status = Status::Warning;
                s->statusText = "installed, but on Steam beta branch '" + gi.betaKey + "' - set Betas to None and update to the release branch.";
            } else if (midUpdate) {
                s->status = Status::Warning;
                s->statusText = "installed, but mid-update or verifying in Steam - let it finish, then re-check.";
            } else {
                s->status = Status::OK;
                std::string ver = (gi.manifestFound && !gi.buildId.empty())
                    ? (" - build " + gi.buildId + verDisp)
                    : (m.config.gameVersion.empty() ? "" : (" - confirm it's on v" + m.config.gameVersion + " for this list"));
                std::string pf = PathUnderProgramFiles(m.gameDir) ? "  (under Program Files - hardlink deploys may need admin)" : "";
                s->statusText = NarrowU8(m.gameDir) + plat + ver + pf;
            }
        }
    }

    if (Step* s = Find(m, "phantomliberty")) {
        if (m.gameDir.empty()) {
            s->status = Status::Manual;
            s->statusText = "find the game first";
        } else {
            bool have = PhantomLibertyInstalled(m.gameDir);
            if (!m.config.requiresPhantomLiberty) {
                s->status = Status::OK;
                s->statusText = have ? "installed" : "not required by this list";
            } else if (have) {
                s->status = Status::OK;
                s->statusText = "installed";
            } else {
                s->status = Status::Warning;
                s->statusText = "required by this list but not found - install the Phantom Liberty expansion";
            }
        }
    }

    if (Step* s = Find(m, "disk")) {
        std::wstring instPath = CheckRootW(m);
        std::wstring dlPath = (m.mode == Mode::MO2) ? instPath : Widen(std::string(m.downloadsPath));
        std::wstring instRoot = DriveRootOf(instPath);
        uint64_t instFree = FreeBytesOnDrive(instPath);
        std::string need = std::to_string(m.config.requiredGB) + " GB";
        std::string iRoot = NarrowU8(instRoot);
        bool sameDrive = dlPath.empty() || lstrcmpiW(instRoot.c_str(), DriveRootOf(dlPath).c_str()) == 0;
        if (instFree == 0) {
            s->status = Status::Warning; s->statusText = "could not read free space on " + iRoot;
        } else if (sameDrive) {
            if (instFree >= requiredBytes) { s->status = Status::OK; s->statusText = GbStr(instFree) + " free on " + iRoot; }
            else { s->status = Status::Warning; s->statusText = GbStr(instFree) + " free on " + iRoot + " - need ~" + need; }
        } else {
            std::wstring dlRoot = DriveRootOf(dlPath);
            std::string dRoot = NarrowU8(dlRoot);
            uint64_t dlFree = FreeBytesOnDrive(dlPath);
            std::string both = "install " + GbStr(instFree) + " on " + iRoot + ", downloads " + GbStr(dlFree) + " on " + dRoot;
            if (instFree >= requiredBytes && dlFree >= requiredBytes) { s->status = Status::OK; s->statusText = both; }
            else { s->status = Status::Warning; s->statusText = both + " - want ~" + need + " on each"; }
        }
    }

    if (Step* s = Find(m, "sync")) {
        if (PathUnderCloudSync(CheckRootW(m))) { s->status = Status::Warning; s->statusText = "this folder is inside a cloud-synced folder"; }
        else { s->status = Status::OK; s->statusText = "not in OneDrive/Dropbox"; }
    }

    if (Step* s = Find(m, "writeperm")) {
        if (PathUnderProgramFiles(CheckRootW(m))) { s->status = Status::Warning; s->statusText = "inside Program Files - pick a folder outside it (e.g. on another drive)"; }
        else if (!DirWritable(CheckRootW(m))) { s->status = Status::Warning; s->statusText = "this folder isn't writable - pick a different location"; }
        else { s->status = Status::OK; s->statusText = "writable, outside Program Files"; }
    }

    if (Step* s = Find(m, "redmod")) {
        if (RedmodInstalled(m.gameDir)) { s->status = Status::OK; s->statusText = "installed"; }
        else { s->status = Status::Missing; s->statusText = "not detected - install the free REDmod DLC"; }
        if (nonSteam) {
            s->url.clear();
            if (s->status != Status::OK) s->guide = std::string("Install the free REDmod component through ") + PlatformName(m.platform) + ".";
        }
    }

    if (Step* s = Find(m, "vortex_ntfs")) {
        std::wstring probe = m.gameDir.empty() ? InstallW(m) : m.gameDir;
        std::string fsn = DriveFilesystem(probe);
        if (fsn.empty()) { s->status = Status::Warning; s->statusText = "could not read the drive filesystem"; }
        else if (fsn == "NTFS") { s->status = Status::OK; s->statusText = "NTFS - hardlinks supported"; }
        else { s->status = Status::Warning; s->statusText = fsn + " - NOT hardlink-capable; Vortex needs NTFS"; }
    }

    if (Step* s = Find(m, "steamupdate")) {
        if (nonSteam) { s->status = Status::OK; s->statusText = std::string("Steam only - skipped on ") + PlatformName(m.platform); }
        else {
            int b = SteamAutoUpdateBehavior();
            if (b == 1) { s->status = Status::OK; s->statusText = "set to update only on launch"; }
            else if (b < 0) { s->status = Status::Warning; s->statusText = "could not read Steam setting"; }
            else { s->status = Status::Warning; s->statusText = "set to auto-update"; }
        }
    }


    if (Step* s = Find(m, "vcredist")) {
        if (VcRedistInstalled()) { s->status = Status::OK; s->statusText = "installed"; }
        else { s->status = Status::Missing; s->statusText = "not installed"; }
    }

    if (Step* s = Find(m, "dotnet")) {
        std::string v = DotNet8DesktopVersion();
        if (!v.empty()) { s->status = Status::OK; s->statusText = v + " installed"; }
        else { s->status = Status::Missing; s->statusText = "not installed"; }
    }

    if (Step* s = Find(m, "directx")) {
        if (DirectXLegacyInstalled()) { s->status = Status::OK; s->statusText = "installed"; }
        else { s->status = Status::Missing; s->statusText = "not installed"; }
    }

    if (Step* s = Find(m, "defender")) {
        s->status = Status::Manual;
        s->statusText = m.thirdPartyAv.empty()
            ? "optional - add exclusions so Defender doesn't quarantine files mid-install"
            : (m.thirdPartyAv + " is your active antivirus, so Defender exclusions won't apply");
    }

    if (Step* s = Find(m, "longpaths")) {
        if (s->status == Status::Done) { s->statusText = "enabled (restart Windows to apply)"; }
        else if (LongPathsEnabled() == 1) { s->status = Status::OK; s->statusText = "enabled"; }
        else { s->status = Status::Missing; s->statusText = "disabled - deep mod paths can fail to extract"; }
    }

    if (Step* s = Find(m, "clean")) {
        if (m.gameDir.empty()) { s->status = Status::Manual; s->statusText = "find the game first"; }
        else if (!m.cleanScanned) { s->status = Status::Unknown; s->statusText = "not scanned yet"; }
        else if (m.cleanCount == 0) { s->status = Status::OK; s->statusText = "vanilla - nothing to move"; }
        else { s->status = Status::Warning; s->statusText = std::to_string(m.cleanCount) + " mod item(s) in the game folder"; }
    }

    if (Step* s = Find(m, "wabbajack")) {
        if (m.wabbajackReady || WabbajackInstalled(WjW(m), &m.wabbajackExe)) {
            m.wabbajackReady = true;
            s->status = Status::Done;
            std::wstring wjExe = (fs::path(WjW(m)) / L"Wabbajack.exe").wstring();
            bool inWjFolder = !m.wabbajackExe.empty() && lstrcmpiW(m.wabbajackExe.c_str(), wjExe.c_str()) == 0;
            s->statusText = inWjFolder ? "in your Wabbajack folder"
                          : m.wabbajackExe.empty() ? "found on this PC"
                          : ("found - " + NarrowU8(m.wabbajackExe));
        } else { s->status = Status::Missing; s->statusText = "not downloaded yet"; }
    }

    if (Step* s = Find(m, "vortex")) {
        if (m.vortexReady || VortexInstalled(&m.vortexExe)) { s->status = Status::Done; s->statusText = "installed"; m.vortexReady = true; }
        else { s->status = Status::Missing; s->statusText = "not installed yet"; }
    }

    if (Step* s = Find(m, "vortex_extension")) {
        if (VortexCyberpunkExtension()) { s->status = Status::Done; s->statusText = "game managed - extension installed"; }
        else { s->status = Status::Manual; s->statusText = "manage the game - Vortex prompts for the extension, then restarts"; }
    }

    {
        VortexCpSettings vs = ReadVortexCpSettings();
        if (Step* s = Find(m, "vortex_staging")) {
            if (vs.stagingPath.empty()) { s->status = Status::Manual; s->statusText = "in Vortex, make sure the Mod Staging Folder is set - outside the Vortex app folder, same drive as the game"; }
            else if (!m.gameDir.empty() && !SameDrive(vs.stagingPath, m.gameDir)) {
                s->status = Status::Warning;
                s->statusText = "staging " + vs.stagingPath + " is on a different drive than the game - hardlinks need the same drive";
            } else { s->status = Status::Done; s->statusText = "staging " + vs.stagingPath; }
        }
        if (Step* s = Find(m, "vortex_hardlink")) {
            if (vs.deployMethod == "hardlink_activator") { s->status = Status::Done; s->statusText = "Hardlink deployment"; }
            else if (!vs.deployMethod.empty()) { s->status = Status::Warning; s->statusText = "currently " + PrettyDeployMethod(vs.deployMethod) + " - set it to Hardlink"; }
            else { s->status = Status::Manual; s->statusText = "in Vortex, make sure Deployment Method is set to Hardlink"; }
        }
    }

    if (Step* s = Find(m, "vortex_redmod")) {
        s->status = Status::OK;
        s->statusText = "both V2077 toggles default to off - just confirm under Cyberpunk's Preferences";
    }

    if (Step* s = Find(m, "vortex_profile")) {
        int pc = VortexProfileCount();
        s->statusText = pc >= 1 ? (std::to_string(pc) + " profile(s) found - make sure yours is created + enabled") : "";
    }

    for (const char* id : { "vortex_profile", "vortex_collection", "wj_findlist" }) {
        if (Step* s = Find(m, id)) { if (s->status != Status::Done) s->status = Status::Manual; }
    }

    if (Step* s = Find(m, "m_gpu")) {
        std::string g = GpuName();
        m.gpuVendor = GpuVendorOf(g);
        s->status = Status::Manual;
        s->statusText = g.empty() ? "update your GPU drivers" : g;
    }

    if (Step* s = Find(m, "m_av")) {
        if (m.thirdPartyAv.empty()) { s->status = Status::OK; s->statusText = "none detected - Windows Defender is fine"; }
        else { s->status = Status::Warning; s->statusText = m.thirdPartyAv + " detected - remove it; it breaks the mod manager's file system"; }
    }

    m.vortexManagingCp = VortexCyberpunkExtension() || (VortexProfileCount() >= 0);
    m.vortexDownloadsDir = VortexDownloadsDir();
}

void ApplyStep(Model& m, const std::string& id) {
    Step* s = Find(m, id.c_str());
    if (!s) return;
    s->status = Status::Working;
    if (id == "vcredist") ApplyVcRedist(m, *s);
    else if (id == "dotnet") ApplyDotNet(m, *s);
    else if (id == "directx") ApplyDirectX(m, *s);
    else if (id == "steamupdate") ApplyTweak(m, *s, SetSteamAutoUpdateLaunchOnly, "Setting Steam to update only on launch");
    else if (id == "wabbajack") DownloadWabbajack(m);
    else if (id == "vortex") DownloadVortex(m);
}

void RunAdminBatch(Model& m, const std::vector<std::string>& ops) {
    if (ops.empty()) return;
    for (const auto& op : ops) { Step* s = Find(m, op.c_str()); if (s) s->status = Status::Working; }

    wchar_t t[1024] = {};
    DWORD n = ::GetTempPathW(1024, t);
    std::wstring resultFile = std::wstring(t, n) + L"cbsetup-adminresult.txt";
    std::error_code ec;
    fs::remove(fs::path(resultFile), ec);

    std::wstring args = L"--admin-batch --result \"" + resultFile + L"\" --install \"" + InstallW(m)
        + L"\" --downloads \"" + ToolCacheDir() + L"\"";
    for (const auto& op : ops) args += L" " + Widen(op);

    SetLabel(m, "Installing system components (administrator)");
    Append(m, "Requesting administrator access for the system installs...");
    std::string err;
    int code = RunElevatedSelf(args, err);
    if (code == -2) {
        Append(m, "  Administrator access was declined - the system installs were skipped.");
    } else if (code < 0) {
        Append(m, "  Could not start the elevated step: " + err);
    } else {
        std::ifstream f(fs::path(resultFile), std::ios::binary);
        if (f) {
            std::string body((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
            {
                std::lock_guard<std::mutex> lk(m.logMtx);
                m.log += body;
            }
            if (body.find("Long paths enabled") != std::string::npos) {
                if (Step* s = Find(m, "longpaths")) s->status = Status::Done;
            }
        }
    }
    SetLabel(m, "");
}

void RunAllAuto(Model& m) {
    Append(m, "Running the selected preinstallation actions...");
    std::vector<std::string> adminOps;
    if (m.optSysInstalls) { adminOps.push_back("vcredist"); adminOps.push_back("dotnet"); adminOps.push_back("directx"); }
    if (m.optLongPaths) adminOps.push_back("longpaths");
    if (!adminOps.empty()) RunAdminBatch(m, adminOps);
    if (m.cancelRequested.load()) { Append(m, "Cancelled."); SetLabel(m, ""); return; }
    if (m.optSteamTweaks) {
        if (m.platform == Platform::GOG || m.platform == Platform::Epic) {
            Append(m, std::string("Skipping the Steam auto-update tweak - this is a ") + PlatformName(m.platform) + " install.");
        } else {
            ApplyStep(m, "steamupdate");
        }
    }
    if (m.cancelRequested.load()) { Append(m, "Cancelled."); SetLabel(m, ""); return; }
    if (m.optClean) RunFullClean(m);
    if (m.cancelRequested.load()) { Append(m, "Cancelled."); SetLabel(m, ""); return; }
    if (m.optDownload) {
        if (m.mode == Mode::Vortex) DownloadVortex(m);
        else DownloadWabbajack(m);
    }
    Append(m, "Finished the selected actions. Follow the on-screen manual steps to finish.");
    m.summaryPending = true;
    SetLabel(m, "");
}

void RunCleanScan(Model& m) {
    if (m.gameDir.empty()) { Append(m, "Clean scan: game folder not found."); return; }
    SetLabel(m, "Scanning the game folder");
    cleanslate::FullCleanReport plan = cleanslate::PlanFullClean(m.gameDir);
    m.cleanScanned = true;
    m.cleanCount = plan.dirs + plan.files;
    m.cleanScanReport = plan;
    m.cleanAppData = LocalAppDataLeftovers();
    Append(m, "Clean scan: " + std::to_string(m.cleanCount) + " mod item(s) in the game folder, "
              + std::to_string(m.cleanAppData.size()) + " in AppData.");
    SetLabel(m, "");
}

CleanPreflight PrecheckClean(Model& m) {
    CleanPreflight pf;
    if (m.gameDir.empty()) {
        pf.blockers.push_back("Game folder not found - find or set the Cyberpunk 2077 install first.");
        return pf;
    }
    std::error_code ec;
    fs::path root(m.gameDir);
    if (!fs::exists(root / L"bin\\x64\\Cyberpunk2077.exe", ec)) {
        pf.blockers.push_back("That folder isn't a valid Cyberpunk 2077 install (bin\\x64\\Cyberpunk2077.exe missing).");
        return pf;
    }

    if (IsProcessRunning(L"Cyberpunk2077.exe"))
        pf.blockers.push_back("Cyberpunk 2077 is running - close the game before cleaning.");
    if (IsProcessRunning(L"Vortex.exe"))
        pf.blockers.push_back("Vortex is running - close it before cleaning (it holds the deployed files).");
    if (IsProcessRunning(L"ModOrganizer.exe"))
        pf.blockers.push_back("Mod Organizer 2 is running - close it before cleaning.");

    if (PathUnderCloudSync(m.gameDir))
        pf.blockers.push_back("The game is inside a cloud-synced folder (OneDrive/Dropbox) - move it out before cleaning.");

    std::wstring driveRoot = DriveRootOf(m.gameDir);
    UINT dt = driveRoot.empty() ? DRIVE_UNKNOWN : ::GetDriveTypeW(driveRoot.c_str());
    if (dt == DRIVE_REMOTE)
        pf.blockers.push_back("The game is on a network drive - cleaning is only supported on a local fixed drive.");
    else if (dt == DRIVE_REMOVABLE || dt == DRIVE_CDROM)
        pf.blockers.push_back("The game is on a removable drive - cleaning is only supported on a local fixed drive.");

    pf.plan = cleanslate::PlanFullClean(m.gameDir);
    if (pf.blockers.empty() && !pf.plan.ok)
        pf.blockers.push_back("Could not read the game folder to plan the clean.");

    m.cleanAppData = LocalAppDataLeftovers();
    pf.ok = pf.blockers.empty();
    return pf;
}

void RunFullClean(Model& m) {
    if (m.cancelRequested.load()) { Append(m, "Clean cancelled."); return; }
    CleanPreflight pf = PrecheckClean(m);
    if (!pf.ok) {
        Append(m, "Clean blocked - not safe to proceed:");
        for (const auto& b : pf.blockers) Append(m, "  - " + b);
        if (Step* s = Find(m, "clean")) { s->status = Status::Warning; s->statusText = "blocked - see log"; }
        SetLabel(m, "");
        return;
    }
    SetLabel(m, "Cleaning the game folder to vanilla");
    Append(m, "Moving mod files out of the game folder (reversible backup, keeping vanilla)...");
    cleanslate::QuarantineResult qr = cleanslate::Quarantine(pf.plan, m.gameDir, NowStamp());
    Append(m, "  Moved " + std::to_string(qr.moved) + " game-folder item(s) to a reversible backup"
              + (qr.failed ? (" (" + std::to_string(qr.failed) + " failed)") : "") + ".");
    if (qr.moved > 0) {
        std::string entry = "{\"type\":\"clean\",\"gameDir\":";
        cleanslate::JsonEsc(entry, NarrowU8(m.gameDir));
        entry += "}";
        JournalAppend(entry);
    }
    std::string adMsg;
    int adMoved = 0;
    std::vector<std::pair<std::wstring, std::wstring>> adMoves;
    QuarantineLocalAppData(adMsg, adMoved, &adMoves);
    Append(m, "  " + adMsg);
    for (const auto& mv : adMoves) {
        std::string entry = "{\"type\":\"appdata\",\"src\":";
        cleanslate::JsonEsc(entry, NarrowU8(mv.first));
        entry += ",\"backup\":";
        cleanslate::JsonEsc(entry, NarrowU8(mv.second));
        entry += "}";
        JournalAppend(entry);
    }

    std::error_code ec;
    fs::path root(m.gameDir);
    bool exeOk = fs::exists(root / L"bin\\x64\\Cyberpunk2077.exe", ec);
    bool archiveOk = fs::exists(root / L"archive\\pc\\content", ec);
    if (exeOk && archiveOk)
        Append(m, "  Vanilla game files verified intact (executable + base archives present).");
    else
        Append(m, "  WARNING: vanilla files look incomplete after the clean - verify/repair the game now to restore them.");

    if (m.platform == Platform::GOG || m.platform == Platform::Epic)
        Append(m, std::string("  Then use ") + PlatformName(m.platform) + "'s verify/repair to restore any vanilla files this moved.");
    else
        Append(m, "  Then run Steam -> Verify integrity (steam://validate/1091500) to restore vanilla files.");
    cleanslate::CleanReport after = cleanslate::Scan(m.gameDir);
    m.cleanScanned = true;
    m.cleanCount = after.loose;
    SetLabel(m, "");
}

void DownloadWabbajack(Model& m) {
    Step* s = Find(m, "wabbajack");
    if (s) s->status = Status::Working;
    std::error_code ec;
    fs::create_directories(fs::path(WjW(m)), ec);
    std::wstring dest = (fs::path(WjW(m)) / L"Wabbajack.exe").wstring();
    SetLabel(m, "Downloading Wabbajack");
    Append(m, "Downloading the latest Wabbajack...");
    m.dlDone = 0;
    m.dlTotal = 0;
    ProgressFn prog = [&m](uint64_t done, uint64_t total) { m.dlDone = done; m.dlTotal = total; };
    std::string err;
    if (!HttpGetToFile(kWabbajack, dest, prog, err, &m.cancelRequested)) {
        Append(m, "  FAILED to download Wabbajack: " + err);
        if (s) s->status = Status::Failed;
        return;
    }
    Append(m, "  Done: Wabbajack downloaded to your Wabbajack folder.");
    m.wabbajackReady = true;
    if (s) s->status = Status::Done;
}

void SendListToWabbajack(Model& m) {
    const std::string pfx = "https://www.wabbajack.org/search/";
    std::string machineUrl = (m.config.wabbajackUrl.rfind(pfx, 0) == 0) ? m.config.wabbajackUrl.substr(pfx.size()) : std::string();
    if (machineUrl.empty()) {
        Append(m, "This list has no Wabbajack machine URL configured - opening the gallery so you can find it.");
        OpenUrl(Widen(std::string(WabbajackGalleryUrl())));
        return;
    }

    std::error_code ec;
    std::wstring wjExe;
    std::wstring inFolder = (fs::path(WjW(m)) / L"Wabbajack.exe").wstring();
    if (fs::exists(fs::path(inFolder), ec)) wjExe = inFolder;
    else if (WabbajackInstalled(WjW(m), &m.wabbajackExe) && !m.wabbajackExe.empty() && fs::exists(fs::path(m.wabbajackExe), ec)) wjExe = m.wabbajackExe;

    if (wjExe.empty()) {
        Append(m, "Wabbajack isn't installed yet - download it with the button in step 2, then send the list.");
        return;
    }

    std::wstring dir = fs::path(wjExe).parent_path().wstring();
    std::wstring cli;
    for (const wchar_t* name : { L"Wabbajack CLI.exe", L"Wabbajack-CLI.exe", L"wabbajack-cli.exe" }) {
        std::wstring c = (fs::path(dir) / name).wstring();
        if (fs::exists(fs::path(c), ec)) { cli = c; break; }
    }

    if (!cli.empty()) {
        std::wstring dlDir = Widen(std::string(m.downloadsPath));
        fs::create_directories(fs::path(InstallW(m)), ec);
        fs::create_directories(fs::path(dlDir), ec);
        std::wstring args = L"install -m " + ArgvQuote(Widen(machineUrl))
            + L" -o " + ArgvQuote(InstallW(m)) + L" -d " + ArgvQuote(dlDir);
        Append(m, "Starting Wabbajack on " + machineUrl + " with your folders preset. Premium Nexus is hands-off; free accounts click each download.");
        LaunchDetachedArgs(cli, args);
        return;
    }

    LaunchDetachedArgs(wjExe, ArgvQuote(Widen("wabbajack://" + machineUrl)));
    Append(m, "Opening Wabbajack on " + machineUrl + " - set Install + Downloads to your folders above, then Install.");
}

void DownloadVortex(Model& m) {
    Step* s = Find(m, "vortex");
    if (s) s->status = Status::Working;
    std::wstring url;
    std::string name = "vortex-setup.exe";
    std::string err;
    if (!m.config.vortexUrl.empty()) {
        url = Widen(m.config.vortexUrl);
        Append(m, "Using the curator-pinned Vortex installer...");
    } else {
        SetLabel(m, "Finding the latest Vortex");
        Append(m, "Looking up the latest stable Vortex release...");
        if (!GithubLatestExeAsset("Nexus-Mods/Vortex", "setup", "Vortex", url, name, err)) {
            Append(m, "  FAILED to find Vortex: " + err);
            if (s) s->status = Status::Failed;
            return;
        }
    }
    std::error_code ec;
    fs::create_directories(fs::path(ToolCacheDir()), ec);
    std::wstring dest = (fs::path(ToolCacheDir()) / Widen(name)).wstring();
    SetLabel(m, "Downloading " + name);
    Append(m, "  Downloading " + name + "...");
    m.dlDone = 0;
    m.dlTotal = 0;
    ProgressFn prog = [&m](uint64_t done, uint64_t total) { m.dlDone = done; m.dlTotal = total; };
    if (!HttpGetToFile(url, dest, prog, err, &m.cancelRequested)) {
        Append(m, "  FAILED to download Vortex: " + err);
        if (s) s->status = Status::Failed;
        return;
    }
    if (!VerifyInstaller(m, dest, nullptr)) {
        Append(m, "  Opening the Vortex download page so you can install it manually.");
        OpenUrl(kVortexPg);
        if (s) s->status = Status::Warning;
        return;
    }
    SetLabel(m, "Installing Vortex");
    Append(m, "  Running the Vortex installer...");
    int code = RunProcess(dest, L"/S", err);
    if (InstallerSucceeded(code)) {
        Append(m, "  Done: Vortex installed.");
        m.vortexReady = true;
        if (s) s->status = Status::Done;
    } else {
        Append(m, "  Silent install didn't complete - opening the installer for you to finish.");
        LaunchDetached(dest);
        if (s) s->status = Status::Warning;
    }
}

void InstallVortexCollection(Model& m) {
    std::string slug = CollectionSlug(m.config.collectionUrl);
    if (slug.empty()) {
        Append(m, "No collection link is configured, so there's no Vortex link to open.");
        return;
    }
    std::string rev = m.config.collectionRevision;
    if (rev.empty()) {
        SetLabel(m, "Finding the latest collection revision");
        Append(m, "Looking up the latest collection revision from Nexus...");
        rev = ResolveCollectionRevision(slug);
    }
    if (!rev.empty()) {
        std::string url = "nxm://cyberpunk2077/collections/" + slug + "/revisions/" + rev;
        Append(m, "Opening Vortex to add the collection (revision " + rev + ")...");
        Append(m, "  In Vortex: pick your fresh profile, then Install Now.");
        OpenUrl(Widen(url));
    } else {
        Append(m, "Couldn't auto-resolve the revision - opening the collection page; click 'Add to Vortex' there.");
        OpenUrl(Widen(m.config.collectionUrl));
    }
    SetLabel(m, "");
}

void InstallGpuApp(Model& m) {
    std::wstring url;
    std::string label;
    std::wstring fname;
    std::wstring page;
    if (m.gpuVendor == "nvidia") {
        url = Widen(m.config.nvidiaAppUrl); label = "NVIDIA App"; fname = L"NVIDIA_App.exe";
        page = L"https://www.nvidia.com/software/nvidia-app/";
    } else if (m.gpuVendor == "amd") {
        url = Widen(m.config.amdAppUrl); label = "AMD Adrenalin"; fname = L"AMD_Adrenalin.exe";
        page = L"https://www.amd.com/en/support/download/drivers.html";
    } else {
        Append(m, "Couldn't tell your GPU vendor - grab the NVIDIA App or AMD Adrenalin from their sites.");
        return;
    }
    std::error_code ec;
    fs::create_directories(fs::path(ToolCacheDir()), ec);
    std::wstring dest = (fs::path(ToolCacheDir()) / fname).wstring();
    SetLabel(m, "Downloading " + label);
    Append(m, "Downloading " + label + "...");
    m.dlDone = 0;
    m.dlTotal = 0;
    ProgressFn prog = [&m](uint64_t done, uint64_t total) { m.dlDone = done; m.dlTotal = total; };
    std::string err;
    if (!HttpGetToFile(url, dest, prog, err, &m.cancelRequested)) {
        Append(m, "  Couldn't download " + label + " (link may have changed) - opening the vendor page instead.");
        OpenUrl(page);
        SetLabel(m, "");
        return;
    }
    const char* signer = (m.gpuVendor == "nvidia") ? "NVIDIA Corporation" : "Advanced Micro Devices";
    if (!VerifyInstaller(m, dest, signer)) {
        Append(m, "  Opening the vendor page so you can download the installer yourself.");
        OpenUrl(page);
        SetLabel(m, "");
        return;
    }
    Append(m, "  Launching the " + label + " installer - follow its prompts, then update your driver inside it.");
    LaunchDetached(dest);
    SetLabel(m, "");
}

void UndoAll(Model& m) {
    auto entries = JournalRead();
    if (entries.empty()) { Append(m, "Nothing to undo - this tool hasn't changed anything yet."); return; }
    SetLabel(m, "Undoing changes");
    Append(m, "Undoing the changes this tool made...");
    for (auto it = entries.rbegin(); it != entries.rend(); ++it) {
        try {
            simdjson::dom::parser p;
            simdjson::dom::element e;
            if (p.parse(simdjson::padded_string(*it)).get(e) != simdjson::SUCCESS) continue;
            std::string_view type;
            if (e["type"].get(type) != simdjson::SUCCESS) continue;
            std::string t(type);
            std::string_view sa, sb;
            if (t == "steam_au") {
                if (e["acf"].get(sa) == simdjson::SUCCESS && e["old"].get(sb) == simdjson::SUCCESS &&
                    RestoreSteamAutoUpdate(Widen(std::string(sa)), std::string(sb)))
                    Append(m, "  Restored Steam auto-update setting.");
            } else if (t == "clean") {
                std::wstring backup = cleanslate::FindLatestQuarantine(m.gameDir);
                if (!backup.empty()) {
                    cleanslate::Restore(m.gameDir, backup);
                    Append(m, "  Restored the cleaned files from backup.");
                }
            } else if (t == "appdata") {
                std::string_view bv;
                if (e["src"].get(sa) == simdjson::SUCCESS && e["backup"].get(bv) == simdjson::SUCCESS) {
                    std::wstring src = Widen(std::string(sa));
                    std::wstring backup = Widen(std::string(bv));
                    std::error_code ec2;
                    if (fs::exists(fs::path(backup), ec2) && !fs::exists(fs::path(src), ec2)) {
                        fs::rename(fs::path(backup), fs::path(src), ec2);
                        if (!ec2) Append(m, "  Restored an AppData folder from backup.");
                    }
                }
            }
        } catch (...) {
        }
    }
    JournalClear();
    Append(m, "Undo complete.");
    SetLabel(m, "");
}

void LaunchWabbajack(Model& m) {
    std::error_code ec;
    std::wstring exe = m.wabbajackExe;
    if (exe.empty() || !fs::exists(fs::path(exe), ec))
        exe = (fs::path(WjW(m)) / L"Wabbajack.exe").wstring();
    if (!fs::exists(fs::path(exe), ec)) return;
    if (!VerifyInstaller(m, exe, nullptr)) {
        Append(m, "  Not launching it - download a fresh copy from wabbajack.org.");
        OpenUrl(L"https://www.wabbajack.org");
        return;
    }
    LaunchDetached(exe);
}

}
