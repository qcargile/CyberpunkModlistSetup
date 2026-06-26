#include "CleanCore.h"
#include "Util.h"

#include <windows.h>
#include <simdjson.h>

#include <algorithm>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>

namespace fs = std::filesystem;

namespace cleanslate {

namespace {

std::wstring ToLowerW(std::wstring s) {
    if (!s.empty()) ::CharLowerBuffW(s.data(), (DWORD)s.size());
    return s;
}

std::wstring NormPathW(std::wstring s) {
    for (auto& c : s) if (c == L'/') c = L'\\';
    if (!s.empty()) ::CharLowerBuffW(s.data(), (DWORD)s.size());
    return s;
}

std::string NormRel(std::string s) {
    for (auto& c : s) { if (c == '/') c = '\\'; if (c >= 'A' && c <= 'Z') c = (char)(c + 32); }
    return s;
}

std::wstring RelativeTo(const std::wstring& full, const std::wstring& root) {
    if (full.size() > root.size() + 1 &&
        ::CompareStringOrdinal(full.data(), (int)root.size(), root.data(), (int)root.size(), TRUE) == CSTR_EQUAL) {
        size_t start = root.size();
        while (start < full.size() && (full[start] == L'\\' || full[start] == L'/')) ++start;
        return full.substr(start);
    }
    return fs::path(full).filename().wstring();
}

bool IsFramework(const std::wstring& fnameLower) {
    static const wchar_t* kFw[] = {
        L"red4ext.dll", L"cyber_engine_tweaks.asi", L"codeware.dll", L"archivexl.dll",
        L"tweakxl.dll", L"redhottools.dll", L"input_loader.dll", L"mod_settings.dll",
        L"redfilesystem.dll", L"reddata.dll", L"redsocket.dll", L"version.dll", L"winmm.dll",
        L"powrprof.dll", L"d3d11.dll", L"scc_lib.dll",
    };
    for (auto* f : kFw) if (fnameLower == f) return true;
    return false;
}

bool IsManagerMarker(const std::wstring& fnameLower) {
    return fnameLower == L"__folder_managed_by_vortex"
        || fnameLower == L"__delete_if_empty"
        || fnameLower == L".stub";
}

bool IsVanillaBaselineFile(const std::string& relLower) {
    static const char* kVanilla[] = {
        "engine\\config\\platform\\pc\\rendering.ini",
        "engine\\config\\platform\\pc\\platformgameplay.ini",
    };
    for (auto* v : kVanilla) if (relLower == v) return true;
    return false;
}

enum class PathProbe { OpenFailed, OutsideRoot, InsideRoot };

PathProbe ProbeFinalPath(const fs::path& filePath, const std::wstring& gameRootNorm) {
    std::wstring p = filePath.wstring();
    HANDLE h = ::CreateFileW(p.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                             nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) return PathProbe::OpenFailed;
    std::wstring buf(1024, L'\0');
    DWORD n = ::GetFinalPathNameByHandleW(h, buf.data(), (DWORD)buf.size(), FILE_NAME_NORMALIZED);
    if (n >= buf.size()) {
        buf.resize(n);
        n = ::GetFinalPathNameByHandleW(h, buf.data(), (DWORD)buf.size(), FILE_NAME_NORMALIZED);
    }
    ::CloseHandle(h);
    if (n == 0 || n >= buf.size()) return PathProbe::OpenFailed;
    buf.resize(n);
    std::wstring full;
    if (buf.compare(0, 8, L"\\\\?\\UNC\\") == 0) full = L"\\\\" + buf.substr(8);
    else if (buf.compare(0, 4, L"\\\\?\\") == 0) full = buf.substr(4);
    else full = buf;
    std::wstring fpl = NormPathW(full);
    if (fpl.size() < gameRootNorm.size() || fpl.compare(0, gameRootNorm.size(), gameRootNorm) != 0) return PathProbe::OutsideRoot;
    return (fpl.size() == gameRootNorm.size() || fpl[gameRootNorm.size()] == L'\\') ? PathProbe::InsideRoot : PathProbe::OutsideRoot;
}

bool IsPhysicalInGame(const fs::path& filePath, const std::wstring& gameRootNorm) {
    return ProbeFinalPath(filePath, gameRootNorm) == PathProbe::InsideRoot;
}

}

const char* ManagerName(Manager m) noexcept {
    switch (m) {
        case Manager::None:   return "None";
        case Manager::MO2:    return "MO2";
        case Manager::Vortex: return "Vortex";
        default:              return "Unknown";
    }
}

const char* FileClassName(FileClass c) noexcept {
    switch (c) {
        case FileClass::Managed:        return "managed";
        case FileClass::ManagedInfra:   return "managed-infra";
        case FileClass::Loose:          return "loose";
        case FileClass::OutdatedOrphan: return "orphan";
    }
    return "?";
}

ReferenceVerdict ProbeReference(const std::wstring& virtualPath, const std::wstring& gameDir) noexcept {
    std::wstring rootNorm = NormPathW(gameDir);
    while (!rootNorm.empty() && rootNorm.back() == L'\\') rootNorm.pop_back();
    switch (ProbeFinalPath(fs::path(virtualPath), rootNorm)) {
        case PathProbe::OutsideRoot: return ReferenceVerdict::ResolvedOutsideRoot;
        case PathProbe::InsideRoot:  return ReferenceVerdict::ResolvedInsideRoot;
        default:                     return ReferenceVerdict::OpenFailed;
    }
}

Manager DetectManager(const std::wstring& gameDir, bool* mo2Running) noexcept {
    std::error_code ec;
    fs::path root(gameDir);
    bool usvfs = (::GetModuleHandleW(L"usvfs_x64.dll") != nullptr);
    if (mo2Running) *mo2Running = usvfs;
    if (usvfs) return Manager::MO2;
    if (fs::exists(root / L"vortex.deployment.json", ec)) return Manager::Vortex;
    if (fs::exists(root / L"V2077", ec)) return Manager::Vortex;
    return Manager::None;
}

bool ParseVortexManifest(const std::wstring& manifestPath,
                         std::unordered_map<std::string, std::string>& sourceByRel) noexcept {
    try {
        std::ifstream f(fs::path(manifestPath), std::ios::binary);
        if (!f) return false;
        std::string buf((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        if (buf.empty()) return false;
        simdjson::padded_string json(buf);
        simdjson::dom::parser parser;
        simdjson::dom::element root;
        if (parser.parse(json).get(root) != simdjson::SUCCESS) return false;
        simdjson::dom::array files;
        if (root["files"].get(files) != simdjson::SUCCESS) return false;
        for (auto file : files) {
            std::string_view rel;
            if (file["relPath"].get(rel) != simdjson::SUCCESS) continue;
            std::string source;
            std::string_view sv;
            if (file["source"].get(sv) == simdjson::SUCCESS) source.assign(sv);
            sourceByRel[NormRel(std::string(rel))] = source;
        }
        return true;
    } catch (...) {
        return false;
    }
}

CleanReport Scan(const std::wstring& gameDir,
                 const std::unordered_set<std::string>* mo2Overlay) noexcept {
    CleanReport r;
    std::error_code ec;
    fs::path root(gameDir);
    if (!fs::exists(root / L"bin\\x64\\Cyberpunk2077.exe", ec)) return r;
    r.gameDir = NarrowU8(gameDir);

    r.manager = DetectManager(gameDir, &r.mo2Running);

    std::unordered_map<std::string, std::string> managed;
    ParseVortexManifest((root / L"vortex.deployment.json").wstring(), managed);
    bool vortexActive = (r.manager == Manager::Vortex) && !managed.empty();
    bool vortexPurged = (r.manager == Manager::Vortex) && !vortexActive;
    r.vortexWillRedeploy = (r.manager == Manager::Vortex);

    std::wstring detail;
    if (vortexActive)
        detail = L"Vortex (active deployment manifest present; mods hardlinked into the game folder)";
    else if (vortexPurged)
        detail = L"Vortex (no active deployment - purged or pre-deploy; only un-managed leftovers remain)";
    else if (r.manager == Manager::MO2)
        detail = L"MO2 (USVFS active; game folder kept clean, mods overlaid virtually)";
    else
        detail = L"None detected (no Vortex footprint, no MO2 VFS)";
    r.managerDetail = NarrowU8(detail);

    std::wstring rootNorm = NormPathW(gameDir);
    while (!rootNorm.empty() && rootNorm.back() == L'\\') rootNorm.pop_back();

    auto classify = [&](const std::wstring& relW, const fs::path& full, bool knownLoader = false) {
        ScannedFile sf;
        sf.relPath = NarrowU8(relW);
        std::string relLower = NormRel(sf.relPath);
        std::wstring fnameLower = ToLowerW(full.filename().wstring());
        if (IsManagerMarker(fnameLower)) return;
        if (IsVanillaBaselineFile(relLower)) return;
        ++r.scanned;

        if (knownLoader || IsFramework(fnameLower)) {
            sf.cls = FileClass::ManagedInfra;
            sf.reason = "framework / loader (expected for a modded install)";
            ++r.managedInfra;
        } else if (mo2Overlay) {
            if (mo2Overlay->count(relLower)) {
                sf.cls = FileClass::Managed;
                sf.reason = "overlaid from the MO2 mods folder";
                ++r.managed;
            } else {
                sf.cls = FileClass::Loose;
                sf.reason = "in the game folder but not overlaid by any enabled mod - leftover/manual";
                ++r.loose;
            }
        } else if (vortexActive) {
            auto it = managed.find(relLower);
            if (it != managed.end()) {
                sf.cls = FileClass::Managed;
                sf.reason = "deployed by Vortex";
                sf.sourceMod = it->second;
                ++r.managed;
            } else {
                sf.cls = FileClass::Loose;
                sf.reason = "not in the Vortex deployment manifest - leftover or manually installed";
                ++r.loose;
            }
        } else if (r.manager == Manager::MO2) {
            if (IsPhysicalInGame(full, rootNorm)) {
                sf.cls = FileClass::Loose;
                sf.reason = "physically in the game folder; MO2 keeps it clean - leftover/manual";
                ++r.loose;
            } else {
                sf.cls = FileClass::Managed;
                sf.reason = "redirected by the mod manager (VFS)";
                ++r.managed;
            }
        } else {
            sf.cls = FileClass::Loose;
            sf.reason = vortexPurged
                ? "left in the game folder after a Vortex purge - runtime-generated or modified after deploy, not managed by Vortex"
                : "in the game folder with no active mod manager - manual or leftover install";
            ++r.loose;
        }
        if (sf.cls == FileClass::Loose) r.findings.push_back(std::move(sf));
    };

    static const wchar_t* kModDirs[] = {
        L"r6\\scripts", L"r6\\tweaks", L"red4ext\\plugins", L"bin\\x64\\plugins",
        L"r6\\storages", L"r6\\audioware", L"engine\\config\\platform\\pc",
        L"mods", L"archive\\pc\\mod",
    };
    const uint32_t kCap = 200000;
    for (auto* sub : kModDirs) {
        fs::path d = root / sub;
        if (!fs::exists(d, ec)) continue;
        for (fs::recursive_directory_iterator it(d, fs::directory_options::skip_permission_denied, ec), end;
             it != end && r.scanned < kCap; it.increment(ec)) {
            if (ec) { ec.clear(); continue; }
            if (!it->is_regular_file(ec)) continue;
            classify(RelativeTo(it->path().wstring(), gameDir), it->path());
        }
    }

    static const wchar_t* kLoaders[] = {
        L"bin\\x64\\winmm.dll", L"bin\\x64\\version.dll", L"bin\\x64\\d3d11.dll",
        L"bin\\x64\\powrprof.dll", L"bin\\x64\\global.ini", L"bin\\x64\\LICENSE",
        L"engine\\tools\\scc_lib.dll",
    };
    for (auto* sub : kLoaders) {
        fs::path f = root / sub;
        if (fs::exists(f, ec)) classify(std::wstring(sub), f, true);
    }

    r.ok = true;
    return r;
}

FullCleanReport PlanFullClean(const std::wstring& gameDir) noexcept {
    FullCleanReport r;
    std::error_code ec;
    fs::path root(gameDir);
    if (!fs::exists(root / L"bin\\x64\\Cyberpunk2077.exe", ec)) return r;
    r.gameDir = NarrowU8(gameDir);
    r.manager = DetectManager(gameDir, nullptr);

    auto addDir = [&](const wchar_t* sub, const char* reason) {
        if (fs::exists(root / sub, ec)) {
            r.items.push_back({ NarrowU8(std::wstring(sub)), true, reason });
            ++r.dirs;
        }
    };
    auto addFile = [&](const wchar_t* sub, const char* reason) {
        if (fs::exists(root / sub, ec)) {
            r.items.push_back({ NarrowU8(std::wstring(sub)), false, reason });
            ++r.files;
        }
    };

    addDir(L"archive\\pc\\mod", "mod archives - not in a clean install");
    addDir(L"red4ext", "RED4ext framework + plugins - not in a clean install");
    addDir(L"bin\\x64\\plugins", "ASI / CET / RED4ext plugins - not in a clean install");
    addDir(L"r6\\scripts", "RedScript mods - not in a clean install");
    addDir(L"r6\\tweaks", "TweakXL mods - not in a clean install");
    addDir(L"r6\\storages", "mod storage - not in a clean install");
    addDir(L"r6\\audioware", "Audioware mods - not in a clean install");

    fs::path modsDir = root / L"mods";
    if (fs::exists(modsDir, ec)) {
        for (fs::directory_iterator it(modsDir, ec), end; it != end; it.increment(ec)) {
            if (ec) { ec.clear(); break; }
            if (!it->is_directory(ec)) continue;
            r.items.push_back({ NarrowU8(RelativeTo(it->path().wstring(), gameDir)), true,
                                "installed REDmod mod - not in a clean install" });
            ++r.dirs;
        }
    }

    addDir(L"V2077", "Vortex footprint (load order) - not in a clean install");
    addFile(L"vortex.deployment.json", "Vortex deployment manifest - not in a clean install");

    addFile(L"bin\\x64\\winmm.dll", "mod loader proxy - not in a clean install");
    addFile(L"bin\\x64\\version.dll", "mod loader proxy - not in a clean install");
    addFile(L"bin\\x64\\d3d11.dll", "mod loader proxy - not in a clean install");
    addFile(L"bin\\x64\\powrprof.dll", "mod loader proxy - not in a clean install");

    addFile(L"bin\\x64\\dxgi.dll", "ReShade proxy - not in a clean install");
    addFile(L"bin\\x64\\opengl32.dll", "ReShade/ENB proxy - not in a clean install");
    addFile(L"bin\\x64\\ReShade.ini", "ReShade config - not in a clean install");
    addFile(L"bin\\x64\\ReShadePreset.ini", "ReShade preset - not in a clean install");
    addDir(L"bin\\x64\\reshade-shaders", "ReShade shaders - not in a clean install");
    addDir(L"bin\\x64\\reshade-presets", "ReShade presets - not in a clean install");
    addDir(L"bin\\x64\\enbseries", "ENB - not in a clean install");
    addFile(L"bin\\x64\\enblocal.ini", "ENB config - not in a clean install");
    addFile(L"bin\\x64\\enbseries.ini", "ENB config - not in a clean install");
    addFile(L"bin\\x64\\enbhost.exe", "ENB host - not in a clean install");
    addFile(L"bin\\x64\\d3dcompiler_46e.dll", "ENB shader compiler - not in a clean install");

    fs::path ecp = root / L"engine\\config\\platform\\pc";
    if (fs::exists(ecp, ec)) {
        for (fs::directory_iterator it(ecp, ec), end; it != end; it.increment(ec)) {
            if (ec) { ec.clear(); break; }
            if (!it->is_regular_file(ec)) continue;
            std::string rel = NarrowU8(RelativeTo(it->path().wstring(), gameDir));
            if (IsVanillaBaselineFile(NormRel(rel))) continue;
            r.items.push_back({ rel, false, "config override - not in a clean install" });
            ++r.files;
        }
    }

    r.ok = true;
    return r;
}

std::string ToJson(const CleanReport& r) noexcept {
    std::string o;
    o.reserve(4096 + r.findings.size() * 128);
    char num[192];
    o += "{\n  \"tool\": \"CleanSlate\",\n  \"schema\": 1,\n";
    std::snprintf(num, sizeof(num), "  \"timeUnix\": %lld,\n", (long long)std::time(nullptr));
    o += num;
    o += "  \"gameDir\": "; JsonEsc(o, r.gameDir); o += ",\n";
    o += "  \"manager\": \""; o += ManagerName(r.manager); o += "\",\n";
    o += "  \"managerDetail\": "; JsonEsc(o, r.managerDetail); o += ",\n";
    o += std::string("  \"mo2Running\": ") + (r.mo2Running ? "true" : "false") + ",\n";
    o += std::string("  \"vortexWillRedeploy\": ") + (r.vortexWillRedeploy ? "true" : "false") + ",\n";
    o += std::string("  \"clean\": ") + ((r.loose + r.orphan) == 0 ? "true" : "false") + ",\n";
    std::snprintf(num, sizeof(num),
                  "  \"counts\": { \"scanned\": %u, \"managed\": %u, \"managedInfra\": %u, \"loose\": %u, \"orphan\": %u },\n",
                  r.scanned, r.managed, r.managedInfra, r.loose, r.orphan);
    o += num;
    o += "  \"findings\": [";
    for (size_t i = 0; i < r.findings.size(); ++i) {
        const auto& f = r.findings[i];
        if (i) o += ",";
        o += "\n    { \"relPath\": "; JsonEsc(o, f.relPath);
        o += ", \"class\": \""; o += FileClassName(f.cls); o += "\"";
        o += ", \"sourceMod\": "; JsonEsc(o, f.sourceMod);
        o += ", \"reason\": "; JsonEsc(o, f.reason); o += " }";
    }
    o += r.findings.empty() ? "]\n}\n" : "\n  ]\n}\n";
    return o;
}

}
