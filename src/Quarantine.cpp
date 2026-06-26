#include "Quarantine.h"
#include "Util.h"

#include <windows.h>
#include <simdjson.h>

#include <filesystem>
#include <fstream>
#include <iterator>

namespace fs = std::filesystem;

namespace cleanslate {

namespace {

bool MoveFileReversible(const fs::path& src, const fs::path& dst, std::error_code& ec) {
    fs::create_directories(dst.parent_path(), ec);
    if (ec) return false;
    fs::rename(src, dst, ec);
    if (!ec) return true;
    ec.clear();
    fs::copy_file(src, dst, fs::copy_options::overwrite_existing, ec);
    if (ec) return false;
    fs::remove(src, ec);
    return !ec;
}

constexpr const wchar_t* kQuarantineRoot = L"CleanSlate_Quarantine";
constexpr const wchar_t* kManifestName   = L"cleanslate_quarantine.json";

}

static QuarantineResult QuarantineRelPaths(const std::vector<std::string>& rels,
                                           const std::wstring& gameDir, const std::wstring& timestamp) {
    QuarantineResult res;
    try {
        fs::path root(gameDir);
        fs::path backup = root / kQuarantineRoot / timestamp;
        std::error_code ec;
        fs::create_directories(backup, ec);
        if (ec) { res.errors.push_back("could not create backup folder"); return res; }
        res.backupDir = NarrowU8(backup.wstring());

        std::string manifestFiles;
        for (const auto& rel : rels) {
            std::wstring relW = Widen(rel);
            fs::path src = root / relW;
            fs::path dst = backup / relW;
            if (!fs::exists(src, ec)) { ++res.failed; res.errors.push_back("missing: " + rel); continue; }
            std::error_code mec;
            if (!MoveFileReversible(src, dst, mec)) { ++res.failed; res.errors.push_back("move failed: " + rel); continue; }
            ++res.moved;
            if (!manifestFiles.empty()) manifestFiles += ",";
            manifestFiles += "\n    { \"relPath\": ";
            JsonEsc(manifestFiles, rel);
            manifestFiles += " }";
        }

        std::string mj = "{\n  \"tool\": \"CleanSlate\",\n  \"type\": \"quarantine\",\n  \"timestamp\": ";
        JsonEsc(mj, NarrowU8(timestamp));
        mj += ",\n  \"gameDir\": ";
        JsonEsc(mj, NarrowU8(gameDir));
        mj += ",\n  \"files\": [";
        mj += manifestFiles.empty() ? "]" : (manifestFiles + "\n  ]");
        mj += "\n}\n";
        std::ofstream o(backup / kManifestName, std::ios::binary);
        if (o) o.write(mj.data(), (std::streamsize)mj.size());

        res.ok = (res.failed == 0);
    } catch (...) {
        res.errors.push_back("unexpected error during quarantine");
    }
    return res;
}

QuarantineResult Quarantine(const CleanReport& report, const std::wstring& gameDir,
                            const std::wstring& timestamp) noexcept {
    std::vector<std::string> rels;
    for (const auto& f : report.findings)
        if (f.cls == FileClass::Loose) rels.push_back(f.relPath);
    return QuarantineRelPaths(rels, gameDir, timestamp);
}

QuarantineResult Quarantine(const FullCleanReport& plan, const std::wstring& gameDir,
                            const std::wstring& timestamp) noexcept {
    std::vector<std::string> rels;
    for (const auto& item : plan.items) rels.push_back(item.relPath);
    return QuarantineRelPaths(rels, gameDir, timestamp);
}

RestoreResult Restore(const std::wstring& gameDir, const std::wstring& backupDir) noexcept {
    RestoreResult res;
    res.backupDir = NarrowU8(backupDir);
    try {
        fs::path backup(backupDir);
        fs::path manifest = backup / kManifestName;
        std::error_code ec;
        if (!fs::exists(manifest, ec)) { res.errors.push_back("no quarantine manifest in that backup folder"); return res; }

        std::ifstream f(manifest, std::ios::binary);
        if (!f) { res.errors.push_back("could not read manifest"); return res; }
        std::string buf((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        simdjson::padded_string json(buf);
        simdjson::dom::parser parser;
        simdjson::dom::element root;
        if (parser.parse(json).get(root) != simdjson::SUCCESS) { res.errors.push_back("manifest is not valid JSON"); return res; }
        simdjson::dom::array files;
        if (root["files"].get(files) != simdjson::SUCCESS) { res.errors.push_back("manifest has no file list"); return res; }

        fs::path gameRoot(gameDir);
        for (auto file : files) {
            std::string_view rel;
            if (file["relPath"].get(rel) != simdjson::SUCCESS) continue;
            std::wstring relW = Widen(std::string(rel));
            fs::path src = backup / relW;
            fs::path dst = gameRoot / relW;
            if (!fs::exists(src, ec)) { ++res.failed; res.errors.push_back("backup missing: " + std::string(rel)); continue; }
            if (fs::exists(dst, ec)) { ++res.skipped; continue; }
            std::error_code mec;
            if (!MoveFileReversible(src, dst, mec)) { ++res.failed; res.errors.push_back("restore failed: " + std::string(rel)); continue; }
            ++res.restored;
        }
        res.ok = (res.failed == 0);
    } catch (...) {
        res.errors.push_back("unexpected error during restore");
    }
    return res;
}

std::wstring FindLatestQuarantine(const std::wstring& gameDir) noexcept {
    try {
        fs::path dir = fs::path(gameDir) / kQuarantineRoot;
        std::error_code ec;
        if (!fs::exists(dir, ec)) return std::wstring();
        std::wstring bestName;
        for (fs::directory_iterator it(dir, ec), end; it != end; it.increment(ec)) {
            if (ec) break;
            if (!it->is_directory(ec)) continue;
            std::wstring name = it->path().filename().wstring();
            if (name > bestName) bestName = name;
        }
        if (bestName.empty()) return std::wstring();
        return (dir / bestName).wstring();
    } catch (...) {
        return std::wstring();
    }
}

}
