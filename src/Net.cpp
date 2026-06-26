#include "Net.h"

#include <windows.h>
#include <winhttp.h>

#include <simdjson.h>

#include <atomic>
#include <filesystem>
#include <fstream>
#include <vector>

namespace fs = std::filesystem;

namespace cbsetup {

namespace {

struct UrlParts {
    std::wstring  host;
    std::wstring  path;
    INTERNET_PORT port  = 0;
    bool          https = true;
};

bool CrackUrl(const std::wstring& url, UrlParts& out) {
    URL_COMPONENTS uc{};
    uc.dwStructSize = sizeof(uc);
    wchar_t host[512] = {};
    wchar_t path[4096] = {};
    uc.lpszHostName = host;
    uc.dwHostNameLength = 511;
    uc.lpszUrlPath = path;
    uc.dwUrlPathLength = 4095;
    if (!WinHttpCrackUrl(url.c_str(), (DWORD)url.size(), 0, &uc)) return false;
    out.host = host;
    out.path = (path[0] != 0) ? path : L"/";
    out.port = uc.nPort;
    out.https = (uc.nScheme == INTERNET_SCHEME_HTTPS);
    return true;
}

bool DoGet(const std::wstring& url, std::string* toBuf, const std::wstring* toFile,
           const ProgressFn* progress, std::string& err,
           const wchar_t* method = L"GET", const std::string* body = nullptr,
           const wchar_t* postHeaders = nullptr, const std::atomic<bool>* cancel = nullptr) {
    UrlParts u;
    if (!CrackUrl(url, u)) { err = "could not parse the download URL"; return false; }

    HINTERNET hSession = WinHttpOpen(L"ChromeBloodSetup/1.0",
                                     WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                     WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) { err = "network init failed"; return false; }

    HINTERNET hConnect = WinHttpConnect(hSession, u.host.c_str(), u.port, 0);
    if (!hConnect) { WinHttpCloseHandle(hSession); err = "could not connect"; return false; }

    DWORD flags = u.https ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET hReq = WinHttpOpenRequest(hConnect, method, u.path.c_str(), nullptr,
                                        WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!hReq) {
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        err = "could not open request";
        return false;
    }

    bool ok = false;
    std::ofstream fout;
    if (toFile) {
        std::error_code ec;
        fs::create_directories(fs::path(*toFile).parent_path(), ec);
        fout.open(fs::path(*toFile), std::ios::binary | std::ios::trunc);
        if (!fout) { err = "could not write to the downloads folder"; }
    }

    LPCWSTR sendHeaders = WINHTTP_NO_ADDITIONAL_HEADERS;
    DWORD   sendHeadersLen = 0;
    LPVOID  sendBody = WINHTTP_NO_REQUEST_DATA;
    DWORD   sendBodyLen = 0;
    if (body) {
        sendHeaders = postHeaders ? postHeaders : L"Content-Type: application/json\r\n";
        sendHeadersLen = (DWORD)-1L;
        sendBody = (LPVOID)body->data();
        sendBodyLen = (DWORD)body->size();
    }

    if ((!toFile || fout) &&
        WinHttpSendRequest(hReq, sendHeaders, sendHeadersLen, sendBody, sendBodyLen, sendBodyLen, 0) &&
        WinHttpReceiveResponse(hReq, nullptr)) {

        DWORD status = 0, ssz = sizeof(status);
        WinHttpQueryHeaders(hReq, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX, &status, &ssz, WINHTTP_NO_HEADER_INDEX);
        if (status >= 200 && status < 300) {
            uint64_t total = 0;
            DWORD clen = 0, csz = sizeof(clen);
            if (WinHttpQueryHeaders(hReq, WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
                                    WINHTTP_HEADER_NAME_BY_INDEX, &clen, &csz, WINHTTP_NO_HEADER_INDEX))
                total = clen;

            std::vector<char> chunk(65536);
            uint64_t done = 0;
            ok = true;
            for (;;) {
                if (cancel && cancel->load()) { err = "cancelled"; ok = false; break; }
                DWORD avail = 0;
                if (!WinHttpQueryDataAvailable(hReq, &avail)) { err = "download interrupted"; ok = false; break; }
                if (avail == 0) break;
                if (avail > chunk.size()) chunk.resize(avail);
                DWORD read = 0;
                if (!WinHttpReadData(hReq, chunk.data(), avail, &read)) { err = "download read failed"; ok = false; break; }
                if (read == 0) break;
                if (toFile) {
                    fout.write(chunk.data(), read);
                    if (!fout) { err = "could not write the download to disk"; ok = false; break; }
                }
                if (toBuf) toBuf->append(chunk.data(), read);
                done += read;
                if (progress && *progress) (*progress)(done, total);
            }
        } else {
            char b[64];
            std::snprintf(b, sizeof(b), "server returned HTTP %lu", status);
            err = b;
        }
    } else if (err.empty()) {
        err = "the download request failed";
    }

    if (toFile) fout.close();
    WinHttpCloseHandle(hReq);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return ok;
}

}

namespace {

bool LooksLikePE(const std::wstring& path) {
    std::ifstream f(fs::path(path), std::ios::binary);
    if (!f) return false;
    char mz[2] = { 0, 0 };
    f.read(mz, 2);
    return f.gcount() == 2 && mz[0] == 'M' && mz[1] == 'Z';
}

bool WantsExe(const std::wstring& p) {
    size_t n = p.size();
    if (n < 4) return false;
    auto lc = [](wchar_t c) { return (c >= L'A' && c <= L'Z') ? (wchar_t)(c + 32) : c; };
    return lc(p[n - 4]) == L'.' && lc(p[n - 3]) == L'e' && lc(p[n - 2]) == L'x' && lc(p[n - 1]) == L'e';
}

}

bool HttpGetToFile(const std::wstring& url, const std::wstring& destPath,
                   const ProgressFn& progress, std::string& err,
                   const std::atomic<bool>* cancel) noexcept {
    try {
        std::wstring partial = destPath + L".partial";
        std::error_code ec;
        for (int attempt = 1; attempt <= 3; ++attempt) {
            if (cancel && cancel->load()) { fs::remove(fs::path(partial), ec); err = "cancelled"; return false; }
            fs::remove(fs::path(partial), ec);
            err.clear();
            bool ok = DoGet(url, nullptr, &partial, &progress, err, L"GET", nullptr, nullptr, cancel);
            if (ok) {
                if (WantsExe(destPath) && !LooksLikePE(partial)) {
                    err = "the downloaded file isn't a valid installer (the link may have moved)";
                    fs::remove(fs::path(partial), ec);
                    return false;
                }
                fs::remove(fs::path(destPath), ec);
                fs::rename(fs::path(partial), fs::path(destPath), ec);
                if (ec) { fs::remove(fs::path(partial), ec); err = "could not finalize the download"; return false; }
                return true;
            }
            if (err == "cancelled" || (cancel && cancel->load())) { fs::remove(fs::path(partial), ec); err = "cancelled"; return false; }
            ::Sleep((DWORD)attempt * 1500);
        }
        fs::remove(fs::path(partial), ec);
        return false;
    } catch (...) {
        err = "unexpected error during download";
        return false;
    }
}

bool HttpGetToString(const std::wstring& url, std::string& out, std::string& err) noexcept {
    try {
        return DoGet(url, &out, nullptr, nullptr, err);
    } catch (...) {
        err = "unexpected error during request";
        return false;
    }
}

bool HttpPostJson(const std::wstring& url, const std::string& body, std::string& out, std::string& err) noexcept {
    try {
        return DoGet(url, &out, nullptr, nullptr, err, L"POST", &body,
                     L"Content-Type: application/json\r\nApplication-Name: CyberpunkModlistSetup\r\nApplication-Version: 1.0\r\n");
    } catch (...) {
        err = "unexpected error during request";
        return false;
    }
}

bool GithubLatestExeAsset(const std::string& ownerRepo, const std::string& preferContains,
                          const std::string& productName, std::wstring& outUrl,
                          std::string& outName, std::string& err) noexcept {
    std::wstring api = L"https://api.github.com/repos/" +
        std::wstring(ownerRepo.begin(), ownerRepo.end()) + L"/releases/latest";
    std::string body;
    if (!HttpGetToString(api, body, err)) return false;
    try {
        simdjson::dom::parser parser;
        simdjson::dom::element root;
        if (parser.parse(simdjson::padded_string(body)).get(root) != simdjson::SUCCESS) {
            err = "could not read the " + productName + " release info";
            return false;
        }
        simdjson::dom::array assets;
        if (root["assets"].get(assets) != simdjson::SUCCESS) {
            err = "no downloads listed for the latest " + productName + " release";
            return false;
        }
        std::string prefer = preferContains;
        for (auto& c : prefer) c = (char)::tolower((unsigned char)c);

        std::wstring firstUrl;
        std::string  firstName;
        for (auto a : assets) {
            std::string_view name;
            if (a["name"].get(name) != simdjson::SUCCESS) continue;
            std::string n(name);
            if (n.size() < 4) continue;
            std::string lower = n;
            for (auto& c : lower) c = (char)::tolower((unsigned char)c);
            if (lower.substr(lower.size() - 4) != ".exe") continue;
            std::string_view durl;
            if (a["browser_download_url"].get(durl) != simdjson::SUCCESS) continue;
            std::string s(durl);
            std::wstring w(s.begin(), s.end());
            if (firstName.empty()) { firstUrl = w; firstName = n; }
            if (!prefer.empty() && lower.find(prefer) != std::string::npos) {
                outUrl = w;
                outName = n;
                return true;
            }
        }
        if (!firstName.empty()) { outUrl = firstUrl; outName = firstName; return true; }
        err = "no installer (.exe) found in the latest " + productName + " release";
        return false;
    } catch (...) {
        err = "could not parse the " + productName + " release info";
        return false;
    }
}

}
