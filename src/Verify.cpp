#include "Verify.h"

#include "Util.h"

#include <windows.h>
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>

#include <cctype>
#include <vector>

namespace cbsetup {

bool VerifyAuthenticode(const std::wstring& path) noexcept {
    WINTRUST_FILE_INFO fi{};
    fi.cbStruct = sizeof(fi);
    fi.pcwszFilePath = path.c_str();

    WINTRUST_DATA wd{};
    wd.cbStruct = sizeof(wd);
    wd.dwUIChoice = WTD_UI_NONE;
    wd.fdwRevocationChecks = WTD_REVOKE_NONE;
    wd.dwUnionChoice = WTD_CHOICE_FILE;
    wd.pFile = &fi;
    wd.dwStateAction = WTD_STATEACTION_VERIFY;
    wd.dwProvFlags = WTD_SAFER_FLAG;

    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG status = ::WinVerifyTrust((HWND)INVALID_HANDLE_VALUE, &action, &wd);

    wd.dwStateAction = WTD_STATEACTION_CLOSE;
    ::WinVerifyTrust((HWND)INVALID_HANDLE_VALUE, &action, &wd);

    return status == ERROR_SUCCESS;
}

std::wstring FileSignerName(const std::wstring& path) noexcept {
    HCERTSTORE store = nullptr;
    HCRYPTMSG msg = nullptr;
    if (!::CryptQueryObject(CERT_QUERY_OBJECT_FILE, path.c_str(),
                            CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED, CERT_QUERY_FORMAT_FLAG_BINARY,
                            0, nullptr, nullptr, nullptr, &store, &msg, nullptr))
        return L"";

    std::wstring name;
    DWORD sz = 0;
    if (::CryptMsgGetParam(msg, CMSG_SIGNER_INFO_PARAM, 0, nullptr, &sz) && sz > 0) {
        std::vector<BYTE> buf(sz);
        if (::CryptMsgGetParam(msg, CMSG_SIGNER_INFO_PARAM, 0, buf.data(), &sz)) {
            auto* si = reinterpret_cast<CMSG_SIGNER_INFO*>(buf.data());
            CERT_INFO ci{};
            ci.Issuer = si->Issuer;
            ci.SerialNumber = si->SerialNumber;
            PCCERT_CONTEXT cert = ::CertFindCertificateInStore(store,
                X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0, CERT_FIND_SUBJECT_CERT, &ci, nullptr);
            if (cert) {
                DWORD n = ::CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, nullptr, 0);
                if (n > 1) {
                    name.resize(n);
                    ::CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, name.data(), n);
                    if (!name.empty() && name.back() == L'\0') name.pop_back();
                }
                ::CertFreeCertificateContext(cert);
            }
        }
    }

    if (msg) ::CryptMsgClose(msg);
    if (store) ::CertCloseStore(store, 0);
    return name;
}

bool InstallerTrusted(const std::wstring& path, const char* expectedSignerSubstr, std::string& reason) noexcept {
    if (!VerifyAuthenticode(path)) {
        reason = "it isn't validly signed (Authenticode check failed)";
        return false;
    }
    if (expectedSignerSubstr && *expectedSignerSubstr) {
        std::string signer = cleanslate::NarrowU8(FileSignerName(path));
        std::string sl = signer;
        std::string el = expectedSignerSubstr;
        for (auto& c : sl) c = (char)::tolower((unsigned char)c);
        for (auto& c : el) c = (char)::tolower((unsigned char)c);
        if (sl.find(el) == std::string::npos) {
            reason = "it's signed by '" + (signer.empty() ? std::string("unknown") : signer)
                   + "', not the expected publisher (" + expectedSignerSubstr + ")";
            return false;
        }
    }
    return true;
}

}
