#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <string>

namespace cbsetup {

using ProgressFn = std::function<void(uint64_t done, uint64_t total)>;

bool HttpGetToFile(const std::wstring& url, const std::wstring& destPath,
                   const ProgressFn& progress, std::string& err,
                   const std::atomic<bool>* cancel = nullptr) noexcept;

bool HttpGetToString(const std::wstring& url, std::string& out, std::string& err) noexcept;

bool HttpPostJson(const std::wstring& url, const std::string& body, std::string& out, std::string& err) noexcept;

bool GithubLatestExeAsset(const std::string& ownerRepo, const std::string& preferContains,
                          const std::string& productName, std::wstring& outUrl,
                          std::string& outName, std::string& err) noexcept;

}
