#include "GameDetect.h"
#include "Util.h"

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <vector>

namespace fs = std::filesystem;

namespace cleanslate {

namespace {

std::wstring RegStr(HKEY root, const wchar_t* sub, const wchar_t* name) {
    wchar_t buf[1024];
    DWORD sz = sizeof(buf);
    if (::RegGetValueW(root, sub, name, RRF_RT_REG_SZ, nullptr, buf, &sz) == ERROR_SUCCESS)
        return std::wstring(buf);
    return L"";
}

bool IsGameDir(const fs::path& p) {
    std::error_code ec;
    return fs::exists(p / L"bin\\x64\\Cyberpunk2077.exe", ec);
}

std::vector<std::wstring> SteamLibraries() {
    std::vector<std::wstring> libs;
    std::wstring steam = RegStr(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath");
    if (steam.empty()) steam = RegStr(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath");
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

}

std::wstring FindGameDir() noexcept {
    wchar_t exePath[1024] = {};
    if (::GetModuleFileNameW(nullptr, exePath, 1023)) {
        fs::path d = fs::path(exePath).parent_path();
        for (int up = 0; up < 4 && !d.empty(); ++up) {
            if (IsGameDir(d)) return d.wstring();
            d = d.parent_path();
        }
    }
    for (const auto& lib : SteamLibraries()) {
        fs::path g = fs::path(lib) / L"steamapps" / L"common" / L"Cyberpunk 2077";
        if (IsGameDir(g)) return g.wstring();
    }
    return L"";
}

}
