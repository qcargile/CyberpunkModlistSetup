#include "SystemTweaks.h"

#include "Detect.h"
#include "Run.h"
#include "Util.h"

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <iterator>
#include <vector>

namespace fs = std::filesystem;

namespace cbsetup {

namespace {

bool CloseSteamGraceful() {
    if (!SteamRunning()) return true;
    std::wstring exe = SteamExePath();
    if (!exe.empty()) {
        std::string err;
        RunProcess(exe, L"-shutdown", err);
    }
    for (int i = 0; i < 40 && SteamRunning(); ++i) ::Sleep(500);
    return !SteamRunning();
}

bool ReadFileText(const std::wstring& path, std::string& out) {
    std::ifstream in(fs::path(path), std::ios::binary);
    if (!in) return false;
    out.assign((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    return true;
}

bool WriteFileText(const std::wstring& path, const std::string& txt) {
    std::wstring tmp = path + L".cbsetup.tmp";
    {
        std::ofstream out(fs::path(tmp), std::ios::binary | std::ios::trunc);
        if (!out) return false;
        out.write(txt.data(), (std::streamsize)txt.size());
        if (!out.good()) return false;
    }
    std::error_code ec;
    fs::rename(fs::path(tmp), fs::path(path), ec);
    if (ec) {
        fs::remove(fs::path(tmp), ec);
        return false;
    }
    return true;
}

void BackupOnce(const std::wstring& path) {
    std::error_code ec;
    std::wstring bak = path + L".cbsetup.bak";
    if (!fs::exists(fs::path(bak), ec)) fs::copy_file(fs::path(path), fs::path(bak), ec);
}

bool ReplaceQuotedValueAfterKey(std::string& txt, const char* quotedKey, size_t keyLen, const char* newVal,
                               std::string* oldOut = nullptr) {
    size_t k = txt.find(quotedKey);
    if (k == std::string::npos) return false;
    size_t q1 = txt.find('"', k + keyLen);
    if (q1 == std::string::npos) return false;
    size_t q2 = txt.find('"', q1 + 1);
    if (q2 == std::string::npos) return false;
    if (oldOut) *oldOut = txt.substr(q1 + 1, q2 - q1 - 1);
    txt.replace(q1 + 1, q2 - q1 - 1, newVal);
    return true;
}

std::wstring JournalPath() {
    wchar_t buf[1024] = {};
    if (::ExpandEnvironmentStringsW(L"%LOCALAPPDATA%\\CyberpunkModlistSetup", buf, 1024) == 0) return L"";
    return (fs::path(buf) / L"journal.jsonl").wstring();
}

}

void JournalAppend(const std::string& line) noexcept {
    std::wstring p = JournalPath();
    if (p.empty()) return;
    std::error_code ec;
    fs::create_directories(fs::path(p).parent_path(), ec);
    std::ofstream f(fs::path(p), std::ios::app | std::ios::binary);
    if (f) f << line << "\n";
}

std::vector<std::string> JournalRead() noexcept {
    std::vector<std::string> out;
    std::wstring p = JournalPath();
    if (p.empty()) return out;
    std::ifstream f(fs::path(p), std::ios::binary);
    std::string line;
    while (std::getline(f, line)) if (!line.empty()) out.push_back(line);
    return out;
}

void JournalClear() noexcept {
    std::wstring p = JournalPath();
    if (p.empty()) return;
    std::error_code ec;
    fs::remove(fs::path(p), ec);
}

bool JournalHasEntries() noexcept {
    std::wstring p = JournalPath();
    if (p.empty()) return false;
    std::error_code ec;
    return fs::exists(fs::path(p), ec) && fs::file_size(fs::path(p), ec) > 0;
}

bool RestoreSteamAutoUpdate(const std::wstring& acf, const std::string& oldVal) noexcept {
    if (SteamRunning() && !CloseSteamGraceful()) return false;
    std::string txt;
    if (!ReadFileText(acf, txt)) return false;
    if (!ReplaceQuotedValueAfterKey(txt, "\"AutoUpdateBehavior\"", 20, oldVal.c_str())) return false;
    return WriteFileText(acf, txt);
}

bool SetSteamAutoUpdateLaunchOnly(std::string& msg) noexcept {
    std::wstring acf = SteamAppManifestPath(L"1091500");
    if (acf.empty()) { msg = "Cyberpunk's Steam manifest not found - is the game installed via Steam?"; return false; }
    if (!CloseSteamGraceful()) { msg = "Could not close Steam - close it manually, then retry."; return false; }
    std::string txt;
    if (!ReadFileText(acf, txt)) { msg = "Could not read the Steam manifest."; return false; }
    std::string oldVal;
    if (!ReplaceQuotedValueAfterKey(txt, "\"AutoUpdateBehavior\"", 20, "1", &oldVal)) {
        msg = "AutoUpdateBehavior not found in the manifest.";
        return false;
    }
    if (oldVal == "1") { msg = "Steam was already set to update only on launch."; return true; }
    BackupOnce(acf);
    if (!WriteFileText(acf, txt)) { msg = "Could not write the Steam manifest."; return false; }
    std::string entry = "{\"type\":\"steam_au\",\"acf\":";
    cleanslate::JsonEsc(entry, cleanslate::NarrowU8(acf));
    entry += ",\"old\":\"" + oldVal + "\"}";
    JournalAppend(entry);
    msg = "Steam set to update Cyberpunk only on launch.";
    return true;
}

bool EnableLongPaths(std::string& msg) noexcept {
    HKEY h;
    if (::RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\FileSystem", 0, KEY_SET_VALUE, &h) != ERROR_SUCCESS) {
        msg = "Could not open the FileSystem registry key (needs administrator).";
        return false;
    }
    DWORD one = 1;
    LONG r = ::RegSetValueExW(h, L"LongPathsEnabled", 0, REG_DWORD, (const BYTE*)&one, sizeof(one));
    ::RegCloseKey(h);
    if (r != ERROR_SUCCESS) {
        msg = "Could not set LongPathsEnabled.";
        return false;
    }
    msg = "Long paths enabled (restart Windows to apply).";
    return true;
}

struct AdTarget { const wchar_t* rel; const wchar_t* backupName; const char* display; };
static const AdTarget kAdTargets[] = {
    { L"REDEngine", L"REDEngine", "%LOCALAPPDATA%\\REDEngine" },
    { L"CD Projekt Red\\Cyberpunk 2077", L"CDPR-Cyberpunk2077", "%LOCALAPPDATA%\\CD Projekt Red\\Cyberpunk 2077" },
};

std::vector<std::string> LocalAppDataLeftovers() noexcept {
    std::vector<std::string> out;
    wchar_t local[1024] = {};
    if (::ExpandEnvironmentStringsW(L"%LOCALAPPDATA%", local, 1024) == 0) return out;
    std::wstring base = local;
    std::error_code ec;
    for (const auto& t : kAdTargets) {
        if (fs::exists(fs::path(base) / t.rel, ec)) out.push_back(t.display);
    }
    return out;
}

bool QuarantineLocalAppData(std::string& msg, int& moved,
                            std::vector<std::pair<std::wstring, std::wstring>>* outMoves) noexcept {
    moved = 0;
    int failed = 0;
    wchar_t local[1024] = {};
    if (::ExpandEnvironmentStringsW(L"%LOCALAPPDATA%", local, 1024) == 0) {
        msg = "Could not locate your AppData folder.";
        return false;
    }
    std::wstring base = local;
    std::wstring ts = cleanslate::NowStamp();

    std::error_code ec;
    for (const auto& t : kAdTargets) {
        fs::path src = fs::path(base) / t.rel;
        if (!fs::exists(src, ec)) continue;
        fs::path dst = fs::path(base) / (std::wstring(t.backupName) + L".cbsetup-backup-" + ts);
        fs::rename(src, dst, ec);
        if (!ec) {
            ++moved;
            if (outMoves) outMoves->push_back({ src.wstring(), dst.wstring() });
        } else {
            ++failed;
        }
    }

    if (failed > 0) {
        msg = "Moved " + std::to_string(moved) + " AppData leftover(s); "
            + std::to_string(failed) + " could not be moved (in use - close GOG Galaxy / the game, then retry).";
        return moved > 0;
    }
    if (moved > 0) {
        msg = "Moved " + std::to_string(moved) + " Cyberpunk leftover(s) in AppData to a backup.";
        return true;
    }
    msg = "No Cyberpunk leftovers in AppData (already clean).";
    return true;
}

}
