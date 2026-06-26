#pragma once

#include <string>

namespace cbsetup {

bool VerifyAuthenticode(const std::wstring& path) noexcept;

std::wstring FileSignerName(const std::wstring& path) noexcept;

bool InstallerTrusted(const std::wstring& path, const char* expectedSignerSubstr, std::string& reason) noexcept;

}
