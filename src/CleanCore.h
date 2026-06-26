#pragma once
#include <cstdint>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace cleanslate {

enum class Manager { Unknown, None, MO2, Vortex };

enum class FileClass {
    Managed,
    ManagedInfra,
    Loose,
    OutdatedOrphan,
};

struct ScannedFile {
    std::string relPath;
    FileClass   cls = FileClass::Loose;
    std::string sourceMod;
    std::string reason;
};

struct CleanReport {
    bool        ok                 = false;
    Manager     manager            = Manager::Unknown;
    bool        mo2Running         = false;
    bool        vortexWillRedeploy = false;
    std::string gameDir;
    std::string managerDetail;
    uint32_t    scanned       = 0;
    uint32_t    managed       = 0;
    uint32_t    managedInfra  = 0;
    uint32_t    loose         = 0;
    uint32_t    orphan        = 0;
    std::vector<ScannedFile> findings;
};

struct RemovalItem {
    std::string relPath;
    bool        isDir = false;
    std::string reason;
};

struct FullCleanReport {
    bool        ok = false;
    Manager     manager = Manager::Unknown;
    std::string gameDir;
    std::string managerDetail;
    uint32_t    dirs  = 0;
    uint32_t    files = 0;
    std::vector<RemovalItem> items;
};

enum class ReferenceVerdict { OpenFailed, ResolvedOutsideRoot, ResolvedInsideRoot };

const char* ManagerName(Manager m) noexcept;
const char* FileClassName(FileClass c) noexcept;

Manager DetectManager(const std::wstring& gameDir, bool* mo2Running) noexcept;

ReferenceVerdict ProbeReference(const std::wstring& virtualPath, const std::wstring& gameDir) noexcept;

bool ParseVortexManifest(const std::wstring& manifestPath,
                         std::unordered_map<std::string, std::string>& sourceByRel) noexcept;

CleanReport Scan(const std::wstring& gameDir,
                 const std::unordered_set<std::string>* mo2Overlay = nullptr) noexcept;

FullCleanReport PlanFullClean(const std::wstring& gameDir) noexcept;

std::string ToJson(const CleanReport& r) noexcept;

}
