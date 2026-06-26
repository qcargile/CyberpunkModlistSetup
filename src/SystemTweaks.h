#pragma once

#include <string>
#include <utility>
#include <vector>

namespace cbsetup {

bool SetSteamAutoUpdateLaunchOnly(std::string& msg) noexcept;

bool EnableLongPaths(std::string& msg) noexcept;

bool QuarantineLocalAppData(std::string& msg, int& moved,
                            std::vector<std::pair<std::wstring, std::wstring>>* outMoves = nullptr) noexcept;

std::vector<std::string> LocalAppDataLeftovers() noexcept;

void JournalAppend(const std::string& line) noexcept;
std::vector<std::string> JournalRead() noexcept;
void JournalClear() noexcept;
bool JournalHasEntries() noexcept;

bool RestoreSteamAutoUpdate(const std::wstring& acf, const std::string& oldVal) noexcept;

}
