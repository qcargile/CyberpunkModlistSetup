#pragma once
#include "CleanCore.h"

#include <cstdint>
#include <string>
#include <vector>

namespace cleanslate {

struct QuarantineResult {
    bool        ok     = false;
    uint32_t    moved  = 0;
    uint32_t    failed = 0;
    std::string backupDir;
    std::vector<std::string> errors;
};

struct RestoreResult {
    bool        ok       = false;
    uint32_t    restored = 0;
    uint32_t    skipped  = 0;
    uint32_t    failed   = 0;
    std::string backupDir;
    std::vector<std::string> errors;
};

QuarantineResult Quarantine(const CleanReport& report, const std::wstring& gameDir,
                            const std::wstring& timestamp) noexcept;

QuarantineResult Quarantine(const FullCleanReport& plan, const std::wstring& gameDir,
                            const std::wstring& timestamp) noexcept;

RestoreResult Restore(const std::wstring& gameDir, const std::wstring& backupDir) noexcept;

std::wstring FindLatestQuarantine(const std::wstring& gameDir) noexcept;

}
