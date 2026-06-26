#include "Detect.h"

#include "GameDetect.h"
#include "Util.h"

#include <windows.h>
#include <tlhelp32.h>

#include <simdjson.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <cwctype>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;
using cleanslate::Widen;
using cleanslate::NarrowU8;

namespace cbsetup {

namespace {

std::wstring Expand(const wchar_t* s) {
    wchar_t buf[2048];
    DWORD n = ::ExpandEnvironmentStringsW(s, buf, 2048);
    if (n == 0 || n > 2048) return L"";
    return std::wstring(buf);
}

bool RegDword(HKEY root, const wchar_t* sub, const wchar_t* name, DWORD& out) {
    DWORD sz = sizeof(out);
    return ::RegGetValueW(root, sub, name, RRF_RT_REG_DWORD, nullptr, &out, &sz) == ERROR_SUCCESS;
}

std::wstring RegStr(HKEY root, const wchar_t* sub, const wchar_t* name) {
    wchar_t buf[1024];
    DWORD sz = sizeof(buf);
    if (::RegGetValueW(root, sub, name, RRF_RT_REG_SZ, nullptr, buf, &sz) == ERROR_SUCCESS)
        return std::wstring(buf);
    return L"";
}

bool FileExists(const std::wstring& p) {
    std::error_code ec;
    return fs::exists(fs::path(p), ec);
}

std::string ReadTextFile(const std::wstring& p) {
    std::ifstream f(fs::path(p), std::ios::binary);
    if (!f) return "";
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

std::string VdfStringValue(const std::string& txt, const char* quotedKey) {
    size_t k = txt.find(quotedKey);
    if (k == std::string::npos) return "";
    size_t q1 = txt.find('"', k + std::strlen(quotedKey));
    if (q1 == std::string::npos) return "";
    size_t q2 = txt.find('"', q1 + 1);
    if (q2 == std::string::npos) return "";
    return txt.substr(q1 + 1, q2 - q1 - 1);
}

bool HasGameExe(const std::wstring& dir) {
    if (dir.empty()) return false;
    return FileExists((fs::path(dir) / L"bin\\x64\\Cyberpunk2077.exe").wstring());
}

bool FindGogGame(std::wstring& out) {
    const wchar_t* roots[] = {
        L"SOFTWARE\\WOW6432Node\\GOG.com\\Games",
        L"SOFTWARE\\GOG.com\\Games",
    };
    for (const wchar_t* sub : roots) {
        HKEY h;
        if (::RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub, 0, KEY_READ, &h) != ERROR_SUCCESS) continue;
        wchar_t name[256];
        DWORD idx = 0;
        DWORD nlen = 256;
        while (::RegEnumKeyExW(h, idx, name, &nlen, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
            std::wstring full = std::wstring(sub) + L"\\" + name;
            std::wstring path = RegStr(HKEY_LOCAL_MACHINE, full.c_str(), L"path");
            if (HasGameExe(path)) { out = path; ::RegCloseKey(h); return true; }
            ++idx;
            nlen = 256;
        }
        ::RegCloseKey(h);
    }
    return false;
}

bool FindEpicGame(std::wstring& out) {
    std::wstring dir = Expand(L"%ProgramData%\\Epic\\EpicGamesLauncher\\Data\\Manifests");
    std::error_code ec;
    if (dir.empty() || !fs::exists(fs::path(dir), ec)) return false;
    for (fs::directory_iterator it(fs::path(dir), ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (it->path().extension() != L".item") continue;
        std::string buf = ReadTextFile(it->path().wstring());
        if (buf.empty()) continue;
        try {
            simdjson::dom::parser parser;
            simdjson::dom::element root;
            if (parser.parse(simdjson::padded_string(buf)).get(root) != simdjson::SUCCESS) continue;
            std::string_view loc;
            if (root["InstallLocation"].get(loc) != simdjson::SUCCESS) continue;
            std::wstring w = Widen(std::string(loc));
            if (HasGameExe(w)) { out = w; return true; }
        } catch (...) {
        }
    }
    return false;
}

}

bool VcRedistInstalled() noexcept {
    DWORD installed = 0;
    if (RegDword(HKEY_LOCAL_MACHINE,
                 L"SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64",
                 L"Installed", installed) && installed == 1)
        return true;
    if (RegDword(HKEY_LOCAL_MACHINE,
                 L"SOFTWARE\\WOW6432Node\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64",
                 L"Installed", installed) && installed == 1)
        return true;
    return FileExists(Expand(L"%SystemRoot%\\System32\\vcruntime140.dll"));
}

std::string DotNet8DesktopVersion() noexcept {
    std::wstring base = Expand(L"%ProgramFiles%\\dotnet\\shared\\Microsoft.WindowsDesktop.App");
    std::error_code ec;
    if (!fs::exists(fs::path(base), ec)) return "";
    std::string best;
    for (fs::directory_iterator it(fs::path(base), ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (!it->is_directory(ec)) continue;
        std::string name = NarrowU8(it->path().filename().wstring());
        if (name.rfind("8.", 0) == 0 && name > best) best = name;
    }
    return best;
}

bool DirectXLegacyInstalled() noexcept {
    return FileExists(Expand(L"%SystemRoot%\\System32\\d3dx9_43.dll"));
}

std::wstring WingetPath() noexcept {
    std::wstring local = Expand(L"%LOCALAPPDATA%\\Microsoft\\WindowsApps\\winget.exe");
    if (FileExists(local)) return local;
    wchar_t found[1024];
    if (::SearchPathW(nullptr, L"winget.exe", nullptr, 1024, found, nullptr) > 0)
        return std::wstring(found);
    return L"";
}

bool WingetAvailable() noexcept {
    return !WingetPath().empty();
}

std::wstring DriveRootOf(const std::wstring& path) noexcept {
    if (path.size() >= 2 && path[1] == L':') {
        std::wstring r;
        r.push_back(path[0]);
        r += L":\\";
        return r;
    }
    return L"";
}

uint64_t FreeBytesOnDrive(const std::wstring& anyPathOnDrive) noexcept {
    std::wstring root = DriveRootOf(anyPathOnDrive);
    if (root.empty()) return 0;
    ULARGE_INTEGER freeForCaller{};
    if (::GetDiskFreeSpaceExW(root.c_str(), &freeForCaller, nullptr, nullptr))
        return freeForCaller.QuadPart;
    return 0;
}

std::string DriveFilesystem(const std::wstring& anyPathOnDrive) noexcept {
    std::wstring root = DriveRootOf(anyPathOnDrive);
    if (root.empty()) return "";
    wchar_t fsName[64] = {};
    if (::GetVolumeInformationW(root.c_str(), nullptr, 0, nullptr, nullptr, nullptr, fsName, 64))
        return NarrowU8(std::wstring(fsName));
    return "";
}

bool PathUnderCloudSync(const std::wstring& path) noexcept {
    if (path.empty()) return false;
    std::wstring lower = path;
    ::CharLowerBuffW(lower.data(), (DWORD)lower.size());

    std::wstring candidates[] = {
        Expand(L"%OneDrive%"),
        Expand(L"%OneDriveConsumer%"),
        Expand(L"%OneDriveCommercial%"),
        Expand(L"%USERPROFILE%\\Dropbox"),
        Expand(L"%USERPROFILE%\\OneDrive"),
        Expand(L"%USERPROFILE%\\Google Drive"),
    };
    for (auto& c : candidates) {
        if (c.empty() || c[0] == L'%') continue;
        std::wstring cl = c;
        ::CharLowerBuffW(cl.data(), (DWORD)cl.size());
        if (lower.size() >= cl.size() && lower.compare(0, cl.size(), cl) == 0) return true;
    }
    return false;
}

bool PathUnderProgramFiles(const std::wstring& path) noexcept {
    if (path.empty()) return false;
    std::wstring lower = path;
    ::CharLowerBuffW(lower.data(), (DWORD)lower.size());
    const wchar_t* vars[] = { L"%ProgramFiles%", L"%ProgramFiles(x86)%", L"%ProgramW6432%" };
    for (const wchar_t* v : vars) {
        std::wstring c = Expand(v);
        if (c.empty() || c[0] == L'%') continue;
        ::CharLowerBuffW(c.data(), (DWORD)c.size());
        if (lower.size() >= c.size() && lower.compare(0, c.size(), c) == 0) return true;
    }
    return false;
}

bool DirWritable(const std::wstring& path) noexcept {
    if (path.empty()) return false;
    std::error_code ec;
    fs::path p(path);
    while (!p.empty() && !fs::exists(p, ec)) p = p.parent_path();
    if (p.empty()) return false;
    fs::path probe = p / L".cbsetup_write_probe.tmp";
    HANDLE h = ::CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    ::CloseHandle(h);
    return true;
}

int LongPathsEnabled() noexcept {
    DWORD v = 0;
    if (RegDword(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\FileSystem", L"LongPathsEnabled", v))
        return v ? 1 : 0;
    return -1;
}

std::vector<std::wstring> SteamLibraries() noexcept {
    std::vector<std::wstring> libs;
    std::wstring steam = RegStr(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath");
    if (steam.empty())
        steam = RegStr(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath");
    if (steam.empty()) return libs;
    libs.push_back(steam);

    fs::path vdf = fs::path(steam) / L"steamapps" / L"libraryfolders.vdf";
    std::ifstream f(vdf);
    if (!f) return libs;
    std::string line;
    while (std::getline(f, line)) {
        size_t start = line.find_first_not_of(" \t");
        if (start == std::string::npos || line.compare(start, 6, "\"path\"") != 0) continue;
        size_t q1 = line.find('"', start + 6);
        if (q1 == std::string::npos) continue;
        size_t q2 = line.find('"', q1 + 1);
        if (q2 == std::string::npos) continue;
        std::string raw = line.substr(q1 + 1, q2 - q1 - 1);
        std::string unesc;
        for (size_t i = 0; i < raw.size(); ++i) {
            if (raw[i] == '\\' && i + 1 < raw.size() && raw[i + 1] == '\\') { unesc.push_back('\\'); ++i; }
            else unesc.push_back(raw[i]);
        }
        libs.push_back(Widen(unesc));
    }
    return libs;
}

bool RedmodInstalled(const std::wstring& gameDir) noexcept {
    if (!gameDir.empty()) {
        if (FileExists((fs::path(gameDir) / L"tools\\redmod\\bin\\redMod.exe").wstring())) return true;
        if (FileExists((fs::path(gameDir) / L"tools\\redmod\\metadata.json").wstring())) return true;
    }
    for (const auto& lib : SteamLibraries()) {
        std::wstring acf = (fs::path(lib) / L"steamapps" / L"appmanifest_2060310.acf").wstring();
        if (FileExists(acf)) return true;
        std::string txt = ReadTextFile((fs::path(lib) / L"steamapps" / L"appmanifest_1091500.acf").wstring());
        if (!txt.empty() && txt.find("\"2060310\"") != std::string::npos) return true;
    }
    return false;
}

int SteamAutoUpdateBehavior() noexcept {
    for (const auto& lib : SteamLibraries()) {
        std::wstring acf = (fs::path(lib) / L"steamapps" / L"appmanifest_1091500.acf").wstring();
        std::string txt = ReadTextFile(acf);
        if (txt.empty()) continue;
        size_t k = txt.find("\"AutoUpdateBehavior\"");
        if (k == std::string::npos) return -1;
        size_t q1 = txt.find('"', k + 20);
        if (q1 == std::string::npos) return -1;
        size_t q2 = txt.find('"', q1 + 1);
        if (q2 == std::string::npos) return -1;
        std::string v = txt.substr(q1 + 1, q2 - q1 - 1);
        if (v == "0") return 0;
        if (v == "1") return 1;
        if (v == "2") return 2;
        return -1;
    }
    return -1;
}

SteamGameInfo ReadSteamGameInfo(const std::wstring& gameDir) noexcept {
    SteamGameInfo info;
    if (gameDir.empty()) return info;
    std::wstring acf = (fs::path(gameDir).parent_path().parent_path() / L"appmanifest_1091500.acf").wstring();
    std::string txt = ReadTextFile(acf);
    if (txt.empty()) return info;
    info.manifestFound = true;
    info.buildId = VdfStringValue(txt, "\"buildid\"");
    info.targetBuildId = VdfStringValue(txt, "\"TargetBuildID\"");
    info.betaKey = VdfStringValue(txt, "\"BetaKey\"");
    std::string sf = VdfStringValue(txt, "\"StateFlags\"");
    if (!sf.empty()) info.stateFlags = std::strtoull(sf.c_str(), nullptr, 10);
    return info;
}

bool PhantomLibertyInstalled(const std::wstring& gameDir) noexcept {
    if (gameDir.empty()) return false;
    std::error_code ec;
    fs::path ep1 = fs::path(gameDir) / L"archive\\pc\\ep1";
    if (fs::is_directory(ep1, ec)) {
        for (fs::directory_iterator it(ep1, ec), end; it != end; it.increment(ec)) {
            if (ec) { ec.clear(); break; }
            if (it->is_regular_file(ec) && it->path().extension() == L".archive") return true;
        }
    }
    return FileExists((fs::path(gameDir) / L"r6\\cache\\tweakdb_ep1.bin").wstring());
}

std::wstring SteamAppManifestPath(const wchar_t* appid) noexcept {
    std::wstring file = std::wstring(L"appmanifest_") + appid + L".acf";
    for (const auto& lib : SteamLibraries()) {
        std::wstring p = (fs::path(lib) / L"steamapps" / file).wstring();
        if (FileExists(p)) return p;
    }
    return L"";
}

std::wstring SteamExePath() noexcept {
    std::wstring base = RegStr(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath");
    if (base.empty())
        base = RegStr(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath");
    if (base.empty()) return L"";
    return (fs::path(base) / L"steam.exe").wstring();
}

bool IsProcessRunning(const wchar_t* exeName) noexcept {
    HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    bool found = false;
    if (::Process32FirstW(snap, &pe)) {
        do {
            if (::lstrcmpiW(pe.szExeFile, exeName) == 0) { found = true; break; }
        } while (::Process32NextW(snap, &pe));
    }
    ::CloseHandle(snap);
    return found;
}

bool SteamRunning() noexcept {
    return IsProcessRunning(L"steam.exe");
}

int VortexProfileCount() noexcept {
    std::wstring base = Expand(L"%APPDATA%\\Vortex\\cyberpunk2077\\profiles");
    if (base.empty() || base[0] == L'%') return -1;
    std::error_code ec;
    fs::path p(base);
    if (!fs::exists(p, ec)) return -1;
    int n = 0;
    for (fs::directory_iterator it(p, ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (it->is_directory(ec)) ++n;
    }
    return n;
}

namespace {

std::string LevelDbLastValue(const std::wstring& stateDir, const char* key) {
    std::error_code ec;
    fs::path dir(stateDir);
    if (!fs::exists(dir, ec)) return "";

    std::vector<fs::path> files;
    std::vector<fs::path> logs;
    for (fs::directory_iterator it(dir, ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        auto ext = it->path().extension();
        if (ext == L".ldb") files.push_back(it->path());
        else if (ext == L".log") logs.push_back(it->path());
    }
    std::sort(files.begin(), files.end());
    std::sort(logs.begin(), logs.end());
    files.insert(files.end(), logs.begin(), logs.end());

    std::string result;
    size_t klen = std::strlen(key);
    for (const auto& f : files) {
        std::string buf = ReadTextFile(f.wstring());
        if (buf.empty()) continue;
        size_t pos = 0;
        while ((pos = buf.find(key, pos)) != std::string::npos) {
            size_t after = pos + klen;
            size_t q1 = buf.find('"', after);
            if (q1 != std::string::npos && q1 - after < 8) {
                std::string val;
                size_t i = q1 + 1;
                for (; i < buf.size(); ++i) {
                    char c = buf[i];
                    if (c == '\\' && i + 1 < buf.size()) { val.push_back(buf[i + 1]); ++i; continue; }
                    if (c == '"') break;
                    val.push_back(c);
                }
                if (i < buf.size()) result = val;
            }
            pos = after;
        }
    }
    return result;
}

}

bool VortexCyberpunkExtension() noexcept {
    std::wstring game = Expand(L"%APPDATA%\\Vortex\\cyberpunk2077");
    if (!game.empty() && game[0] != L'%' && FileExists(game)) return true;

    std::wstring plugins = Expand(L"%APPDATA%\\Vortex\\plugins");
    if (plugins.empty() || plugins[0] == L'%') return false;
    std::error_code ec;
    fs::path p(plugins);
    if (!fs::exists(p, ec)) return false;
    for (fs::directory_iterator it(p, ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (!it->is_directory(ec)) continue;
        std::wstring name = it->path().filename().wstring();
        for (auto& c : name) c = (wchar_t)::towlower(c);
        if (name.find(L"cyberpunk") != std::wstring::npos) return true;
    }
    return false;
}

VortexCpSettings ReadVortexCpSettings() noexcept {
    VortexCpSettings s;
    std::wstring dir = Expand(L"%APPDATA%\\Vortex\\state.v2");
    if (dir.empty() || dir[0] == L'%') return s;
    s.stagingPath  = LevelDbLastValue(dir, "settings###mods###installPath###cyberpunk2077");
    s.deployMethod = LevelDbLastValue(dir, "settings###mods###activator###cyberpunk2077");
    s.found = !s.stagingPath.empty() || !s.deployMethod.empty();
    return s;
}

std::wstring VortexDownloadsDir() noexcept {
    std::wstring state = Expand(L"%APPDATA%\\Vortex\\state.v2");
    std::string custom;
    if (!state.empty() && state[0] != L'%') custom = LevelDbLastValue(state, "settings###downloads###path");
    std::wstring root = custom.empty() ? Expand(L"%APPDATA%\\Vortex\\downloads") : Widen(custom);
    if (root.empty() || root[0] == L'%') return L"";
    std::error_code ec;
    fs::path cp = fs::path(root) / L"cyberpunk2077";
    fs::path target = fs::exists(cp, ec) ? cp : fs::path(root);
    if (!fs::exists(target, ec)) return L"";
    for (fs::directory_iterator it(target, ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (it->is_regular_file(ec)) return target.wstring();
    }
    return L"";
}

std::string GpuName() noexcept {
    DISPLAY_DEVICEW dd{};
    dd.cb = sizeof(dd);
    if (::EnumDisplayDevicesW(nullptr, 0, &dd, 0))
        return NarrowU8(std::wstring(dd.DeviceString));
    return "";
}

const char* PlatformName(Platform p) noexcept {
    switch (p) {
        case Platform::Steam: return "Steam";
        case Platform::GOG:   return "GOG";
        case Platform::Epic:  return "Epic";
        default:              return "Unknown";
    }
}

Platform ClassifyGameDir(const std::wstring& gameDir) noexcept {
    if (!HasGameExe(gameDir)) return Platform::Unknown;
    std::error_code ec;
    fs::path root(gameDir);
    if (fs::exists(root / L".egstore", ec)) return Platform::Epic;
    for (fs::directory_iterator it(root, ec), end; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); break; }
        if (!it->is_regular_file(ec)) continue;
        std::wstring fn = it->path().filename().wstring();
        if (fn.size() > 8 && fn.compare(0, 8, L"goggame-") == 0) return Platform::GOG;
    }
    if (fs::exists(root.parent_path().parent_path() / L"appmanifest_1091500.acf", ec))
        return Platform::Steam;
    return Platform::Unknown;
}

Platform DetectGameInstall(std::wstring& outDir) noexcept {
    std::wstring p = cleanslate::FindGameDir();
    if (!p.empty()) { outDir = p; return ClassifyGameDir(p); }
    if (FindGogGame(outDir)) return Platform::GOG;
    if (FindEpicGame(outDir)) return Platform::Epic;
    outDir.clear();
    return Platform::Unknown;
}

}
