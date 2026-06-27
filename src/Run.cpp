#include "Run.h"

#include <windows.h>
#include <shellapi.h>

#include <filesystem>
#include <vector>

namespace cbsetup {

int RunProcess(const std::wstring& exe, const std::wstring& args, std::string& err) noexcept {
    std::wstring cmd = L"\"" + exe + L"\"";
    if (!args.empty()) cmd += L" " + args;

    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    if (!CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        err = "could not start the installer";
        return -1;
    }
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

int RunPowerShell(const std::wstring& psCommand, std::string& err) noexcept {
    std::wstring args = L"-NoProfile -ExecutionPolicy Bypass -Command \"" + psCommand + L"\"";
    return RunProcess(L"powershell.exe", args, err);
}

int RunPowerShellCapture(const std::wstring& psCommand, std::string& output, std::string& err) noexcept {
    output.clear();
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    HANDLE rd = nullptr, wr = nullptr;
    if (!::CreatePipe(&rd, &wr, &sa, 0)) { err = "could not create pipe"; return -1; }
    ::SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);

    std::wstring cmd = L"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"" + psCommand + L"\"";
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = wr;
    si.hStdError = wr;
    si.hStdInput = ::GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi{};

    if (!::CreateProcessW(nullptr, buf.data(), nullptr, nullptr, TRUE,
                          CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        ::CloseHandle(rd);
        ::CloseHandle(wr);
        err = "could not start powershell";
        return -1;
    }
    ::CloseHandle(wr);

    char chunk[4096];
    DWORD got = 0;
    while (::ReadFile(rd, chunk, sizeof(chunk), &got, nullptr) && got > 0)
        output.append(chunk, got);
    ::CloseHandle(rd);

    ::WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    ::GetExitCodeProcess(pi.hProcess, &code);
    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);
    return (int)code;
}

void LaunchDetached(const std::wstring& exe) noexcept {
    ::ShellExecuteW(nullptr, L"open", exe.c_str(), nullptr,
                    std::filesystem::path(exe).parent_path().wstring().c_str(), SW_SHOWNORMAL);
}

void LaunchDetachedArgs(const std::wstring& exe, const std::wstring& args) noexcept {
    ::ShellExecuteW(nullptr, L"open", exe.c_str(), args.c_str(),
                    std::filesystem::path(exe).parent_path().wstring().c_str(), SW_SHOWNORMAL);
}

int RunElevatedSelf(const std::wstring& args, std::string& err) noexcept {
    wchar_t self[1024] = {};
    if (::GetModuleFileNameW(nullptr, self, 1024) == 0) { err = "could not resolve own path"; return -1; }
    SHELLEXECUTEINFOW sei{};
    sei.cbSize = sizeof(sei);
    sei.fMask = SEE_MASK_NOCLOSEPROCESS;
    sei.lpVerb = L"runas";
    sei.lpFile = self;
    sei.lpParameters = args.c_str();
    sei.nShow = SW_HIDE;
    if (!::ShellExecuteExW(&sei)) {
        DWORD e = ::GetLastError();
        if (e == ERROR_CANCELLED) { err = "declined"; return -2; }
        err = "could not start the elevated step";
        return -1;
    }
    if (!sei.hProcess) { err = "no elevated process handle"; return -1; }
    ::WaitForSingleObject(sei.hProcess, INFINITE);
    DWORD code = 0;
    ::GetExitCodeProcess(sei.hProcess, &code);
    ::CloseHandle(sei.hProcess);
    return (int)code;
}

bool IsElevated() noexcept {
    BOOL elevated = FALSE;
    HANDLE token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        TOKEN_ELEVATION te{};
        DWORD sz = sizeof(te);
        if (GetTokenInformation(token, TokenElevation, &te, sizeof(te), &sz))
            elevated = te.TokenIsElevated;
        CloseHandle(token);
    }
    return elevated != FALSE;
}

void OpenUrl(const std::wstring& url) noexcept {
    ::ShellExecuteW(nullptr, L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

void OpenFolder(const std::wstring& path) noexcept {
    ::ShellExecuteW(nullptr, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

}
