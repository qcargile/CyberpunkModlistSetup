#pragma once

#include <string>

namespace cbsetup {

int RunProcess(const std::wstring& exe, const std::wstring& args, std::string& err) noexcept;

int RunPowerShell(const std::wstring& psCommand, std::string& err) noexcept;

int RunPowerShellCapture(const std::wstring& psCommand, std::string& output, std::string& err) noexcept;

void LaunchDetached(const std::wstring& exe) noexcept;

int RunElevatedSelf(const std::wstring& args, std::string& err) noexcept;

bool IsElevated() noexcept;

void OpenUrl(const std::wstring& url) noexcept;

void OpenFolder(const std::wstring& path) noexcept;

}
