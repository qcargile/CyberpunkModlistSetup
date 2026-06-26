#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wincodec.h>
#include <d3d11.h>
#include <tchar.h>

#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iterator>
#include <string>
#include <thread>
#include <vector>

#include "Catalog.h"
#include "Config.h"
#include "Detect.h"
#include "Run.h"
#include "SystemTweaks.h"
#include "Util.h"

using namespace cbsetup;
using cleanslate::Widen;

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace {

ID3D11Device*           g_pd3dDevice = nullptr;
ID3D11DeviceContext*    g_pd3dDeviceContext = nullptr;
IDXGISwapChain*         g_pSwapChain = nullptr;
ID3D11RenderTargetView* g_mainRTV = nullptr;

ID3D11ShaderResourceView* g_heroCab  = nullptr;
ID3D11ShaderResourceView* g_heroWtnc = nullptr;
int g_cabW = 0, g_cabH = 0, g_wtncW = 0, g_wtncH = 0;

bool LoadTextureFromFile(const std::wstring& path, ID3D11ShaderResourceView** outSrv, int* outW, int* outH) {
    IWICImagingFactory* factory = nullptr;
    if (FAILED(::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) return false;
    bool ok = false;
    IWICBitmapDecoder* decoder = nullptr;
    if (SUCCEEDED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder))) {
        IWICBitmapFrameDecode* frame = nullptr;
        if (SUCCEEDED(decoder->GetFrame(0, &frame))) {
            IWICFormatConverter* conv = nullptr;
            if (SUCCEEDED(factory->CreateFormatConverter(&conv))
                && SUCCEEDED(conv->Initialize(frame, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom))) {
                UINT w = 0, h = 0;
                conv->GetSize(&w, &h);
                if (w > 0 && h > 0) {
                    std::vector<BYTE> pixels((size_t)w * h * 4);
                    if (SUCCEEDED(conv->CopyPixels(nullptr, w * 4, (UINT)pixels.size(), pixels.data()))) {
                        D3D11_TEXTURE2D_DESC td{};
                        td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
                        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
                        td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                        D3D11_SUBRESOURCE_DATA srd{}; srd.pSysMem = pixels.data(); srd.SysMemPitch = w * 4;
                        ID3D11Texture2D* tex = nullptr;
                        if (SUCCEEDED(g_pd3dDevice->CreateTexture2D(&td, &srd, &tex)) && tex) {
                            D3D11_SHADER_RESOURCE_VIEW_DESC sd{};
                            sd.Format = td.Format; sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1;
                            if (SUCCEEDED(g_pd3dDevice->CreateShaderResourceView(tex, &sd, outSrv))) {
                                *outW = (int)w; *outH = (int)h; ok = true;
                            }
                            tex->Release();
                        }
                    }
                }
            }
            if (conv) conv->Release();
            frame->Release();
        }
        decoder->Release();
    }
    factory->Release();
    return ok;
}

void CreateRenderTarget() {
    ID3D11Texture2D* back = nullptr;
    g_pSwapChain->GetBuffer(0, IID_PPV_ARGS(&back));
    if (back) { g_pd3dDevice->CreateRenderTargetView(back, nullptr, &g_mainRTV); back->Release(); }
}
void CleanupRenderTarget() { if (g_mainRTV) { g_mainRTV->Release(); g_mainRTV = nullptr; } }

bool CreateDeviceD3D(HWND hWnd) {
    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 2;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    D3D_FEATURE_LEVEL fl;
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    if (D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, 2,
            D3D11_SDK_VERSION, &sd, &g_pSwapChain, &g_pd3dDevice, &fl, &g_pd3dDeviceContext) != S_OK)
        return false;
    CreateRenderTarget();
    return true;
}
void CleanupDeviceD3D() {
    CleanupRenderTarget();
    if (g_pSwapChain) { g_pSwapChain->Release(); g_pSwapChain = nullptr; }
    if (g_pd3dDeviceContext) { g_pd3dDeviceContext->Release(); g_pd3dDeviceContext = nullptr; }
    if (g_pd3dDevice) { g_pd3dDevice->Release(); g_pd3dDevice = nullptr; }
}

const float kBaseFontScale = 1.15f;
float g_startupDpi = 1.0f;
float g_fontBase   = 1.0f;
ImFont* g_boldFont = nullptr;

LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam)) return true;
    switch (msg) {
        case WM_SIZE:
            if (g_pd3dDevice && wParam != SIZE_MINIMIZED) {
                CleanupRenderTarget();
                g_pSwapChain->ResizeBuffers(0, (UINT)LOWORD(lParam), (UINT)HIWORD(lParam), DXGI_FORMAT_UNKNOWN, 0);
                CreateRenderTarget();
            }
            return 0;
        case WM_GETMINMAXINFO: {
            UINT dpi = ::GetDpiForWindow(hWnd);
            float s = (dpi >= 96) ? (float)dpi / 96.0f : 1.0f;
            MINMAXINFO* mmi = (MINMAXINFO*)lParam;
            mmi->ptMinTrackSize.x = (LONG)(840 * s);
            mmi->ptMinTrackSize.y = (LONG)(600 * s);
            return 0;
        }
        case WM_DPICHANGED: {
            UINT dpi = HIWORD(wParam);
            float s = (dpi >= 96) ? (float)dpi / 96.0f : 1.0f;
            if (ImGui::GetCurrentContext()) ImGui::GetIO().FontGlobalScale = g_fontBase * s / g_startupDpi;
            const RECT* r = (const RECT*)lParam;
            ::SetWindowPos(hWnd, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top,
                           SWP_NOZORDER | SWP_NOACTIVATE);
            return 0;
        }
        case WM_DESTROY: ::PostQuitMessage(0); return 0;
    }
    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}

struct App {
    Model       m;
    std::thread worker;
};

void Launch(App& app, std::function<void()> job) {
    if (app.m.busy.load()) return;
    if (app.worker.joinable()) app.worker.join();
    app.m.cancelRequested = false;
    app.m.busy = true;
    Model* mp = &app.m;
    app.worker = std::thread([mp, job = std::move(job)]() {
        job();
        mp->pendingRedetect = true;
        mp->busy = false;
    });
}

ImVec4 g_accent      = ImVec4(0.16f, 0.64f, 0.98f, 1.0f);
ImVec4 g_accentHover = ImVec4(0.30f, 0.74f, 1.00f, 1.0f);
const ImVec4 kGreen       = ImVec4(0.30f, 0.84f, 0.52f, 1.0f);
const ImVec4 kAmber       = ImVec4(0.98f, 0.74f, 0.20f, 1.0f);
const ImVec4 kRed         = ImVec4(0.95f, 0.36f, 0.38f, 1.0f);
const ImVec4 kDanger      = ImVec4(0.58f, 0.16f, 0.18f, 1.0f);
const ImVec4 kDangerHover = ImVec4(0.72f, 0.22f, 0.24f, 1.0f);
const ImVec4 kBlue        = ImVec4(0.42f, 0.70f, 1.00f, 1.0f);
const ImVec4 kGray        = ImVec4(0.55f, 0.55f, 0.55f, 1.0f);
const ImVec4 kDim         = ImVec4(0.62f, 0.62f, 0.62f, 1.0f);
const ImVec4 kHead        = ImVec4(0.86f, 0.86f, 0.90f, 1.0f);

ImVec4 DotColor(Status st) {
    switch (st) {
        case Status::OK: case Status::Done: return kGreen;
        case Status::Warning:                return kAmber;
        case Status::Missing: case Status::Failed: return kRed;
        case Status::Working:                return kBlue;
        default:                             return kGray;
    }
}

void StatusDot(Status st) {
    ImVec2 p = ImGui::GetCursorScreenPos();
    float h = ImGui::GetTextLineHeight();
    ImGui::GetWindowDrawList()->AddCircleFilled(ImVec2(p.x + 5.0f, p.y + h * 0.5f), 5.0f,
                                                ImGui::ColorConvertFloat4ToU32(DotColor(st)));
    ImGui::Dummy(ImVec2(15.0f, h));
    ImGui::SameLine(0.0f, 7.0f);
}

bool IsResolved(Status st) {
    return st == Status::OK || st == Status::Done || st == Status::Working;
}

enum class Zone { Checks, Auto, Clean, Manual };

Zone ZoneOf(const Step& s) {
    if (s.group == Group::CleanInstall) return Zone::Clean;
    if (s.group == Group::Manual)       return Zone::Manual;
    if (s.group == Group::Manager)      return s.autoFixable ? Zone::Auto : Zone::Manual;
    if (s.group == Group::Check)        return Zone::Checks;
    return Zone::Auto;
}

const char* StatusTag(Status st) {
    switch (st) {
        case Status::OK:
        case Status::Done:    return "OK  ";
        case Status::Working: return "... ";
        case Status::Warning: return "WARN";
        case Status::Missing: return "MISS";
        case Status::Failed:  return "FAIL";
        case Status::Manual:  return "TODO";
        default:              return "?   ";
    }
}

std::string BuildHealthReport(Model& m) {
    std::string r;
    r += "Cyberpunk Modlist Setup - installation health\n";
    r += "List:     " + m.config.name + "\n";
    r += std::string("Manager:  ") + ((m.mode == Mode::Vortex) ? "Vortex / Collection" : "MO2 / Wabbajack") + "\n";
    r += std::string("Platform: ") + PlatformName(m.platform) + "\n";
    if (!m.gameDir.empty()) r += "Game:     " + cleanslate::NarrowU8(m.gameDir) + "\n";
    r += "\n";
    struct ZL { Zone z; const char* label; };
    const ZL zones[] = {
        { Zone::Checks, "CHECKS" },
        { Zone::Auto,   "AUTOMATED" },
        { Zone::Clean,  "CLEAN INSTALL" },
        { Zone::Manual, "DO YOURSELF" },
    };
    for (const auto& zl : zones) {
        bool any = false;
        for (auto& s : m.steps) {
            if (ZoneOf(s) != zl.z || !StepVisible(m, s)) continue;
            if (!any) { r += std::string(zl.label) + "\n"; any = true; }
            r += std::string("  [") + StatusTag(s.status) + "] " + s.title;
            if (!s.statusText.empty()) r += "  -  " + s.statusText;
            r += "\n";
        }
        if (any) r += "\n";
    }
    return r;
}

ImVec4 AccentForList(ListId id) {
    if (id == ListId::ChromeAndBlood) return ImVec4(0.84f, 0.18f, 0.24f, 1.0f);
    if (id == ListId::Wtnc)           return ImVec4(0.13f, 0.52f, 0.93f, 1.0f);
    return ImVec4(0.34f, 0.46f, 0.60f, 1.0f);
}

bool PrimaryButton(const char* label, const ImVec2& size) {
    float lum = 0.299f * g_accent.x + 0.587f * g_accent.y + 0.114f * g_accent.z;
    ImVec4 txt = (lum > 0.6f) ? ImVec4(0.05f, 0.05f, 0.07f, 1.0f) : ImVec4(1, 1, 1, 1);
    ImGui::PushStyleColor(ImGuiCol_Button,        g_accent);
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, g_accentHover);
    ImGui::PushStyleColor(ImGuiCol_ButtonActive,  g_accent);
    ImGui::PushStyleColor(ImGuiCol_Text,          txt);
    bool r = ImGui::Button(label, size);
    ImGui::PopStyleColor(4);
    return r;
}

void PushBold() { if (g_boldFont) ImGui::PushFont(g_boldFont, 0.0f); }
void PopBold()  { if (g_boldFont) ImGui::PopFont(); }

float RowRightEdge() { return ImGui::GetCursorPosX() + ImGui::GetContentRegionAvail().x; }

void InfoHint(const char* tip) {
    ImGui::SameLine(0, 6);
    ImGui::TextDisabled("(i)");
    if (ImGui::IsItemHovered()) {
        ImGui::BeginTooltip();
        ImGui::PushTextWrapPos(ImGui::GetFontSize() * 26.0f);
        ImGui::TextUnformatted(tip);
        ImGui::PopTextWrapPos();
        ImGui::EndTooltip();
    }
}

bool BrowseForFolder(const std::wstring& initial, std::wstring& out) {
    IFileOpenDialog* dlg = nullptr;
    if (FAILED(::CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dlg))))
        return false;
    DWORD opts = 0;
    dlg->GetOptions(&opts);
    dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
    if (!initial.empty()) {
        std::error_code ec;
        std::filesystem::path p(initial);
        while (!p.empty() && !std::filesystem::exists(p, ec)) {
            std::filesystem::path par = p.parent_path();
            if (par == p) { p.clear(); break; }
            p = par;
        }
        if (!p.empty()) {
            IShellItem* item = nullptr;
            if (SUCCEEDED(::SHCreateItemFromParsingName(p.wstring().c_str(), nullptr, IID_PPV_ARGS(&item)))) {
                dlg->SetFolder(item);
                item->Release();
            }
        }
    }
    bool ok = false;
    HWND owner = (HWND)ImGui::GetMainViewport()->PlatformHandleRaw;
    if (SUCCEEDED(dlg->Show(owner))) {
        IShellItem* res = nullptr;
        if (SUCCEEDED(dlg->GetResult(&res))) {
            PWSTR path = nullptr;
            if (SUCCEEDED(res->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) {
                out.assign(path);
                ::CoTaskMemFree(path);
                ok = true;
            }
            res->Release();
        }
    }
    dlg->Release();
    return ok;
}

void DrawAutoRow(App& app, Step& s) {
    Model& m = app.m;
    ImGui::PushID(s.id.c_str());
    bool busy = m.busy.load();
    float ww = RowRightEdge();

    StatusDot(s.status);
    PushBold(); ImGui::TextUnformatted(s.title.c_str()); PopBold();

    const char* btn = (s.id == "wabbajack") ? "Download"
                    : (s.id == "vortex")    ? "Install"
                    : (s.status == Status::Failed || s.status == Status::Warning) ? "Retry"
                    : IsResolved(s.status) ? "Re-run" : "Run";
    ImGui::SameLine(ww - 100.0f);
    ImGui::BeginDisabled(busy);
    if (ImGui::Button(btn, ImVec2(92, 0))) {
        std::string id = s.id;
        bool adminOp = (id == "vcredist" || id == "dotnet" || id == "directx" || id == "longpaths");
        if (adminOp) Launch(app, [&m, id]() { RunAdminBatch(m, { id }); });
        else Launch(app, [&m, id]() { ApplyStep(m, id); });
    }
    ImGui::EndDisabled();

    if (s.status == Status::Working || !s.statusText.empty()) {
        ImGui::Indent(22.0f);
        if (s.status == Status::Working) {
            ImGui::TextColored(kBlue, "working...");
        } else {
            ImGui::PushTextWrapPos(ww - 8.0f);
            ImGui::TextColored(IsResolved(s.status) ? kDim : (s.status == Status::Warning ? kAmber : kDim), "%s", s.statusText.c_str());
            ImGui::PopTextWrapPos();
        }
        ImGui::Unindent(22.0f);
    }
    ImGui::PopID();
}

void DrawCheckRow(App& app, Step& s) {
    Model& m = app.m;
    ImGui::PushID(s.id.c_str());
    float ww = RowRightEdge();

    StatusDot(s.status);
    PushBold(); ImGui::TextUnformatted(s.title.c_str()); PopBold();

    bool actionable = !IsResolved(s.status);
    bool steam = !(m.platform == Platform::GOG || m.platform == Platform::Epic);
    if (actionable) {
        if (s.id == "game" && steam) {
            if (s.status == Status::Missing) {
                ImGui::SameLine(ww - 175.0f);
                if (ImGui::Button("Get Cyberpunk 2077", ImVec2(165, 0))) OpenUrl(L"steam://store/1091500");
            } else {
                ImGui::SameLine(ww - 130.0f);
                if (ImGui::Button("Open in Steam", ImVec2(120, 0))) OpenUrl(L"steam://nav/games/details/1091500");
            }
        } else if (s.id == "redmod" && !s.url.empty()) {
            ImGui::SameLine(ww - 108.0f);
            if (ImGui::Button("Install", ImVec2(92, 0))) OpenUrl(Widen(s.url));
        } else if (s.id == "phantomliberty" && steam) {
            ImGui::SameLine(ww - 178.0f);
            if (ImGui::Button("Get Phantom Liberty", ImVec2(168, 0))) OpenUrl(L"steam://store/2138330");
        } else if ((s.id == "vortex_hardlink" || s.id == "vortex_extension") && !m.vortexExe.empty()) {
            ImGui::SameLine(ww - 122.0f);
            if (ImGui::Button("Open Vortex", ImVec2(110, 0))) OpenUrl(m.vortexExe);
        } else if (!s.url.empty()) {
            ImGui::SameLine(ww - 108.0f);
            if (ImGui::Button("Open", ImVec2(92, 0))) OpenUrl(Widen(s.url));
        }
    }

    if (!s.statusText.empty()) {
        ImGui::Indent(22.0f);
        ImGui::PushTextWrapPos(ww - 8.0f);
        ImGui::TextColored(IsResolved(s.status) ? kDim : (s.status == Status::Warning ? kAmber : kDim), "%s", s.statusText.c_str());
        ImGui::PopTextWrapPos();
        ImGui::Unindent(22.0f);
    }
    ImGui::PopID();
}

void DrawManualRow(App& app, Step& s, int num) {
    Model& m = app.m;
    ImGui::PushID(s.id.c_str());
    bool busy = m.busy.load();
    float ww = RowRightEdge();

    ImGui::TextColored(g_accent, "%d.", num);
    ImGui::SameLine(0.0f, 8.0f);
    PushBold(); ImGui::TextUnformatted(s.title.c_str()); PopBold();

    if (s.id == "wj_findlist") {
        ImGui::SameLine(ww - 270.0f);
        if (ImGui::Button("Gallery", ImVec2(78, 0))) OpenUrl(Widen(std::string(WabbajackGalleryUrl())));
        if (!s.url.empty()) { ImGui::SameLine(); if (ImGui::Button("Archive", ImVec2(78, 0))) OpenUrl(Widen(s.url)); }
        ImGui::SameLine();
        ImGui::BeginDisabled(busy || !m.wabbajackReady);
        if (ImGui::Button("Open WJ", ImVec2(82, 0))) LaunchWabbajack(m);
        ImGui::EndDisabled();
    } else if (s.id == "vortex_collection") {
        ImGui::SameLine(ww - 200.0f);
        ImGui::BeginDisabled(busy || !m.vortexReady || m.config.collectionUrl.empty());
        if (ImGui::Button("Add to Vortex", ImVec2(120, 0))) Launch(app, [&m]() { InstallVortexCollection(m); });
        ImGui::EndDisabled();
        ImGui::SameLine();
        if (ImGui::Button("Browse", ImVec2(70, 0))) OpenUrl(Widen(std::string(VortexCollectionsUrl())));
    } else if (s.id == "m_gpu") {
        if (!m.gpuVendor.empty()) {
            ImGui::SameLine(ww - 160.0f);
            const char* lbl = (m.gpuVendor == "nvidia") ? "Get NVIDIA App" : "Get AMD Adrenalin";
            ImGui::BeginDisabled(busy);
            if (ImGui::Button(lbl, ImVec2(150, 0))) Launch(app, [&m]() { InstallGpuApp(m); });
            ImGui::EndDisabled();
        } else if (!s.url.empty()) {
            ImGui::SameLine(ww - 108.0f);
            if (ImGui::Button("Open", ImVec2(92, 0))) OpenUrl(Widen(s.url));
        }
    } else if (s.id == "defender") {
        ImGui::SameLine(ww - 162.0f);
        if (ImGui::Button("Windows Security", ImVec2(150, 0))) OpenUrl(L"windowsdefender://");
    } else if (!s.url.empty()) {
        ImGui::SameLine(ww - 108.0f);
        if (ImGui::Button("Open", ImVec2(92, 0))) OpenUrl(Widen(s.url));
    }

    if (!s.statusText.empty() && !IsResolved(s.status)) {
        ImGui::Indent(22.0f);
        ImGui::PushTextWrapPos(ww - 40.0f);
        ImGui::TextColored(kAmber, "%s", s.statusText.c_str());
        ImGui::PopTextWrapPos();
        ImGui::Unindent(22.0f);
    }
    if (!s.guide.empty()) {
        ImGui::Indent(22.0f);
        ImGui::PushTextWrapPos(ww - 40.0f);
        ImGui::TextColored(kDim, "%s", s.guide.c_str());
        ImGui::PopTextWrapPos();
        ImGui::Unindent(22.0f);
    }
    ImGui::Dummy(ImVec2(0, 5));
    ImGui::PopID();
}

void DrawCleanCard(App& app) {
    Model& m = app.m;
    bool busy = m.busy.load();
    Step* s = Find(m, "clean");
    if (!s) return;

    ImGui::BeginChild("cleancard", ImVec2(0, 0), ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_Borders);
    ImGui::PushTextWrapPos(0.0f);
    ImGui::TextColored(kDim, "Moves leftover mod files + game-data to a reversible backup, keeping the vanilla game. Nothing is deleted.");
    ImGui::PopTextWrapPos();
    if (!s->statusText.empty())
        ImGui::TextColored(s->status == Status::Warning ? kAmber : kDim, "%s", s->statusText.c_str());
    ImGui::Dummy(ImVec2(0, 2));
    ImGui::BeginDisabled(busy || m.gameDir.empty());
    if (ImGui::Button("Scan", ImVec2(80, 0))) Launch(app, [&m]() { RunCleanScan(m); });
    ImGui::SameLine();
    ImGui::PushStyleColor(ImGuiCol_Button, kDanger);
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, kDangerHover);
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, kDanger);
    if (ImGui::Button("Clean to vanilla", ImVec2(150, 0))) {
        m.cleanPreflight = PrecheckClean(m);
        ImGui::OpenPopup("confirm_clean");
    }
    ImGui::PopStyleColor(3);
    ImGui::EndDisabled();
    ImGui::SameLine();
    if (m.platform == Platform::GOG || m.platform == Platform::Epic) {
        ImGui::TextColored(kGray, "after cleaning, use %s's verify/repair to restore vanilla files", PlatformName(m.platform));
    } else {
        if (ImGui::Button("Verify files", ImVec2(110, 0))) OpenUrl(L"steam://validate/1091500");
        ImGui::SameLine();
        ImGui::TextColored(kGray, "optional - re-downloads changed files via Steam");
    }

    if (m.cleanScanned && (!m.cleanScanReport.items.empty() || !m.cleanAppData.empty())) {
        bool reshade = false, enb = false;
        for (const auto& it : m.cleanScanReport.items) {
            if (it.reason.find("ReShade") != std::string::npos) reshade = true;
            if (it.reason.find("ENB") != std::string::npos) enb = true;
        }
        ImGui::Dummy(ImVec2(0, 4));
        ImGui::TextColored(kDim, "Detected: %u game-folder + %u AppData - these move to the backup:",
                           (unsigned)m.cleanScanReport.items.size(), (unsigned)m.cleanAppData.size());
        if (reshade || enb) {
            const char* what = (reshade && enb) ? "ReShade + ENB" : reshade ? "ReShade" : "ENB";
            ImGui::TextColored(kAmber, "  %s detected - it'll be moved out with the rest.", what);
        }
        ImGui::BeginChild("scanlist", ImVec2(0, 150), ImGuiChildFlags_Borders, ImGuiWindowFlags_HorizontalScrollbar);
        for (const auto& it : m.cleanScanReport.items)
            ImGui::BulletText("%s%s", it.relPath.c_str(), it.isDir ? "\\" : "");
        for (const auto& ad : m.cleanAppData)
            ImGui::BulletText("%s\\", ad.c_str());
        ImGui::EndChild();
    } else if (m.cleanScanned) {
        ImGui::Dummy(ImVec2(0, 4));
        ImGui::TextColored(kGreen, "Scan complete - the game folder + AppData are already vanilla.");
    }

    ImVec2 center = ImGui::GetMainViewport()->GetCenter();
    ImGui::SetNextWindowPos(center, ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
    if (ImGui::BeginPopupModal("confirm_clean", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
        const CleanPreflight& pf = m.cleanPreflight;
        if (!pf.blockers.empty()) {
            ImGui::TextColored(kRed, "Can't clean safely yet:");
            for (const auto& b : pf.blockers) ImGui::BulletText("%s", b.c_str());
            ImGui::Dummy(ImVec2(0, 4));
            if (ImGui::Button("Close", ImVec2(110, 0))) ImGui::CloseCurrentPopup();
        } else if (pf.plan.dirs + pf.plan.files == 0 && m.cleanAppData.empty()) {
            ImGui::TextUnformatted("The game folder + AppData are already vanilla - nothing to move.");
            ImGui::Dummy(ImVec2(0, 4));
            if (ImGui::Button("Close", ImVec2(110, 0))) ImGui::CloseCurrentPopup();
        } else {
            ImGui::TextUnformatted("Move these to a reversible backup, keeping the vanilla game?");
            ImGui::TextColored(kDim, "%u game-folder + %u AppData item(s) move into a backup. Nothing is deleted.",
                               pf.plan.dirs + pf.plan.files, (unsigned)m.cleanAppData.size());
            ImGui::Dummy(ImVec2(0, 4));
            ImGui::BeginChild("cleanpreview", ImVec2(520, 180), ImGuiChildFlags_Borders, ImGuiWindowFlags_HorizontalScrollbar);
            for (const auto& it : pf.plan.items)
                ImGui::BulletText("%s%s", it.relPath.c_str(), it.isDir ? "\\" : "");
            for (const auto& ad : m.cleanAppData)
                ImGui::BulletText("%s\\", ad.c_str());
            ImGui::EndChild();
            ImGui::Dummy(ImVec2(0, 4));
            if (ImGui::Button("Clean", ImVec2(110, 0))) { Launch(app, [&m]() { RunFullClean(m); }); ImGui::CloseCurrentPopup(); }
            ImGui::SameLine();
            if (ImGui::Button("Cancel", ImVec2(110, 0))) ImGui::CloseCurrentPopup();
        }
        ImGui::EndPopup();
    }
    ImGui::EndChild();
}

void DrawListPick(App& app) {
    Model& m = app.m;
    ImVec2 avail = ImGui::GetContentRegionAvail();
    float padX = ImGui::GetCursorPosX();

    float cardW = avail.x * 0.34f;
    if (cardW < 300.0f) cardW = 300.0f;
    if (cardW > 470.0f) cardW = 470.0f;
    float gap = 30.0f;

    auto imgH = [&](int tw, int th) -> float {
        if (tw <= 0 || th <= 0) return cardW * 0.42f;
        float h = cardW * (float)th / (float)tw;
        float cap = cardW * 0.58f;
        return (h > cap) ? cap : h;
    };
    float ihCab  = imgH(g_cabW, g_cabH);
    float ihWtnc = imgH(g_wtncW, g_wtncH);
    float rowImgH = (ihCab > ihWtnc) ? ihCab : ihWtnc;
    float cardH = rowImgH + 66.0f;

    float blockH = 205.0f + cardH;
    float startY = (avail.y - blockH) * 0.5f;
    if (startY < 14.0f) startY = 14.0f;
    ImGui::Dummy(ImVec2(0, startY));

    auto centerText = [&](const char* txt, ImVec4 col, float scale) {
        if (scale != 1.0f) ImGui::SetWindowFontScale(scale);
        float tw = ImGui::CalcTextSize(txt).x;
        ImGui::SetCursorPosX(padX + (avail.x - tw) * 0.5f);
        ImGui::TextColored(col, "%s", txt);
        if (scale != 1.0f) ImGui::SetWindowFontScale(1.0f);
    };
    const ImU32 kGhostCyan = IM_COL32(0, 225, 255, 150);
    const ImU32 kGhostMag  = IM_COL32(255, 40, 95, 150);
    const ImU32 kNeon      = IM_COL32(0, 225, 255, 220);
    const ImVec4 kNeonV    = ImVec4(0.0f, 0.84f, 0.98f, 0.95f);

    centerText("CYBERPUNK 2077", kNeonV, 1.0f);
    ImGui::Dummy(ImVec2(0, 1));
    {
        const char* txt = "MODLIST SETUP";
        ImFont* font = g_boldFont ? g_boldFont : ImGui::GetFont();
        float fsz = ImGui::GetFontSize() * 2.4f;
        ImVec2 sz = font->CalcTextSizeA(fsz, FLT_MAX, 0.0f, txt);
        ImGui::SetCursorPosX(padX + (avail.x - sz.x) * 0.5f);
        ImVec2 p = ImGui::GetCursorScreenPos();
        ImDrawList* dl = ImGui::GetWindowDrawList();
        dl->AddText(font, fsz, ImVec2(p.x - 2.0f, p.y + 1.0f), kGhostCyan, txt);
        dl->AddText(font, fsz, ImVec2(p.x + 2.0f, p.y - 1.0f), kGhostMag, txt);
        dl->AddText(font, fsz, p, IM_COL32(240, 242, 248, 255), txt);
        ImGui::Dummy(sz);
    }
    ImGui::Dummy(ImVec2(0, 7));
    centerText("Pick a list - we prep your PC for the install.", kDim, 1.15f);

    ImGui::Dummy(ImVec2(0, 12));
    {
        float barW = 260.0f;
        ImGui::SetCursorPosX(padX + (avail.x - barW) * 0.5f);
        ImVec2 b = ImGui::GetCursorScreenPos();
        ImGui::GetWindowDrawList()->AddRectFilled(b, ImVec2(b.x + barW, b.y + 3.0f), kNeon, 1.5f);
        ImGui::Dummy(ImVec2(barW, 3.0f));
    }
    ImGui::Dummy(ImVec2(0, 26));

    auto pick = [&](ListId id) {
        m.list = id;
        m.config = ConfigForList(id);
        m.mode = m.config.collectionUrl.empty() ? Mode::MO2 : Mode::None;
        BuildCatalog(m);
        DetectAll(m);
        SaveState(m);
    };

    auto card = [&](const char* name, const char* sub, ID3D11ShaderResourceView* tex, float ih) -> bool {
        ImGui::PushID(name);
        ImGui::BeginGroup();
        float gx = ImGui::GetCursorPosX();
        bool clicked = false;
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(0, 0));
        if (tex) clicked = ImGui::ImageButton("art", (ImTextureID)(intptr_t)tex, ImVec2(cardW, ih),
                                              ImVec2(0, 0), ImVec2(1, 1), ImVec4(0.09f, 0.10f, 0.13f, 1.0f), ImVec4(1, 1, 1, 1));
        else     clicked = PrimaryButton(name, ImVec2(cardW, ih));
        ImGui::PopStyleVar();
        bool hov = ImGui::IsItemHovered();
        ImGui::GetWindowDrawList()->AddRect(ImGui::GetItemRectMin(), ImGui::GetItemRectMax(),
                                            hov ? kNeon : IM_COL32(255, 255, 255, 26), 3.0f, 0, hov ? 2.0f : 1.0f);
        if (ih < rowImgH) ImGui::Dummy(ImVec2(cardW, rowImgH - ih));
        ImGui::Dummy(ImVec2(0, 10));
        ImGui::SetWindowFontScale(1.25f);
        PushBold();
        float nw = ImGui::CalcTextSize(name).x;
        ImGui::SetCursorPosX(gx + (cardW - nw) * 0.5f);
        ImGui::TextColored(kHead, "%s", name);
        PopBold();
        ImGui::SetWindowFontScale(1.0f);
        float sw = ImGui::CalcTextSize(sub).x;
        ImGui::SetCursorPosX(gx + (cardW - sw) * 0.5f);
        ImGui::TextColored(kDim, "%s", sub);
        ImGui::EndGroup();
        ImGui::PopID();
        return clicked;
    };

    float totalW = cardW * 2 + gap;
    ImGui::SetCursorPosX(padX + (avail.x - totalW) * 0.5f);
    bool pc = card("Chrome & Blood", "MO2 / Wabbajack", g_heroCab, ihCab);
    ImGui::SameLine(0, gap);
    bool pw = card("Welcome to Night City", "Wabbajack or Vortex collection", g_heroWtnc, ihWtnc);

    if (pc) pick(ListId::ChromeAndBlood);
    if (pw) pick(ListId::Wtnc);

    ImGui::Dummy(ImVec2(0, 30));
    centerText("Runs entirely on your PC. No account, no telemetry, nothing uploaded.", kGray, 1.0f);
}

void DrawManagerPick(App& app) {
    Model& m = app.m;
    ImVec2 avail = ImGui::GetContentRegionAvail();
    float padX = ImGui::GetCursorPosX();

    ID3D11ShaderResourceView* tex = (m.list == ListId::ChromeAndBlood) ? g_heroCab
                                  : (m.list == ListId::Wtnc) ? g_heroWtnc : nullptr;
    int tw = (m.list == ListId::ChromeAndBlood) ? g_cabW : g_wtncW;
    int th = (m.list == ListId::ChromeAndBlood) ? g_cabH : g_wtncH;

    float cardW = avail.x * 0.32f;
    if (cardW < 300.0f) cardW = 300.0f;
    if (cardW > 440.0f) cardW = 440.0f;
    float gap = 28.0f;

    float heroH = 0.0f, heroW = 0.0f;
    if (tex && th > 0) {
        heroH = 150.0f;
        heroW = heroH * (float)tw / (float)th;
        float capW = avail.x * 0.62f;
        if (heroW > capW) { heroW = capW; heroH = heroW * (float)th / (float)tw; }
    }

    float blockH = heroH + 230.0f;
    float startY = (avail.y - blockH) * 0.5f;
    if (startY < 14.0f) startY = 14.0f;
    ImGui::Dummy(ImVec2(0, startY));

    auto centerText = [&](const char* txt, ImVec4 col, float scale) {
        if (scale != 1.0f) ImGui::SetWindowFontScale(scale);
        float tWid = ImGui::CalcTextSize(txt).x;
        ImGui::SetCursorPosX(padX + (avail.x - tWid) * 0.5f);
        ImGui::TextColored(col, "%s", txt);
        if (scale != 1.0f) ImGui::SetWindowFontScale(1.0f);
    };

    if (tex && heroH > 0.0f) {
        ImGui::SetCursorPosX(padX + (avail.x - heroW) * 0.5f);
        ImGui::Image((ImTextureID)(intptr_t)tex, ImVec2(heroW, heroH));
        ImGui::Dummy(ImVec2(0, 12));
    }
    PushBold(); centerText(m.config.name.c_str(), kHead, 1.8f); PopBold();
    ImGui::Dummy(ImVec2(0, 4));
    centerText("Pick how you'll install it.", kDim, 1.15f);
    ImGui::Dummy(ImVec2(0, 26));

    auto mgrCard = [&](const char* name, const char* sub) -> bool {
        ImGui::PushID(name);
        ImGui::BeginGroup();
        float gx = ImGui::GetCursorPosX();
        ImGui::SetWindowFontScale(1.1f);
        bool clicked = PrimaryButton(name, ImVec2(cardW, 72.0f));
        ImGui::SetWindowFontScale(1.0f);
        ImGui::Dummy(ImVec2(0, 7));
        float sw = ImGui::CalcTextSize(sub).x;
        ImGui::SetCursorPosX(gx + (sw < cardW ? (cardW - sw) * 0.5f : 0.0f));
        ImGui::PushTextWrapPos(gx + cardW);
        ImGui::TextColored(kDim, "%s", sub);
        ImGui::PopTextWrapPos();
        ImGui::EndGroup();
        ImGui::PopID();
        return clicked;
    };

    float totalW = cardW * 2 + gap;
    ImGui::SetCursorPosX(padX + (avail.x - totalW) * 0.5f);
    bool pMo2 = mgrCard("MO2  /  Wabbajack", "Wabbajack builds the list into Mod Organizer 2");
    ImGui::SameLine(0, gap);
    bool pVtx = mgrCard("Vortex  /  Collection", "Install the Nexus collection through Vortex");

    if (pMo2) { m.mode = Mode::MO2; DetectAll(m); SaveState(m); }
    if (pVtx) { m.mode = Mode::Vortex; DetectAll(m); SaveState(m); }

    ImGui::Dummy(ImVec2(0, 24));
    float backW = 200.0f;
    ImGui::SetCursorPosX(padX + (avail.x - backW) * 0.5f);
    if (ImGui::Button("<-  Back to list choice", ImVec2(backW, 0))) { m.list = ListId::None; m.mode = Mode::None; SaveState(m); }
}

void DrawChecklist(App& app) {
    Model& m = app.m;
    bool busy = m.busy.load();
    const char* modeName = (m.mode == Mode::Vortex) ? "Vortex / Collection" : "MO2 / Wabbajack";

    if (!busy && m.summaryPending.exchange(false)) ImGui::OpenPopup("run_summary");

    ID3D11ShaderResourceView* heroTex = (m.list == ListId::ChromeAndBlood) ? g_heroCab
                                      : (m.list == ListId::Wtnc) ? g_heroWtnc : nullptr;
    int heroTexW = (m.list == ListId::ChromeAndBlood) ? g_cabW : g_wtncW;
    int heroTexH = (m.list == ListId::ChromeAndBlood) ? g_cabH : g_wtncH;

    auto heroInfo = [&]() {
        ImGui::SetWindowFontScale(1.4f);
        PushBold();
        ImGui::TextColored(g_accent, "%s", m.config.name.c_str());
        PopBold();
        ImGui::SetWindowFontScale(1.0f);
        ImGui::TextColored(kDim, "Preinstallation for Cyberpunk 2077  -  %s", modeName);
        ImGui::TextColored(kGray, "Work top to bottom:   1 set your folders     2 run the automated steps     3 clear the checks     4 do your part.");
        ImGui::Dummy(ImVec2(0, 4));
        ImGui::BeginDisabled(busy);
        if (!m.config.website.empty()) { if (ImGui::Button("Website", ImVec2(100, 0))) OpenUrl(Widen(m.config.website)); ImGui::SameLine(); }
        if (!m.config.discord.empty()) { if (ImGui::Button("Discord", ImVec2(100, 0))) OpenUrl(Widen(m.config.discord)); ImGui::SameLine(); }
        if (ImGui::Button("Change list", ImVec2(120, 0))) { m.list = ListId::None; m.mode = Mode::None; SaveState(m); }
        ImGui::EndDisabled();
    };

    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(g_accent.x, g_accent.y, g_accent.z, 0.12f));
    ImGui::BeginChild("hero", ImVec2(0, 0), ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_Borders);
    ImGui::PopStyleColor();
    ImGui::Dummy(ImVec2(0, 4));
    if (heroTex && heroTexH > 0) {
        float ih = 130.0f * g_startupDpi;
        float iw = ih * (float)heroTexW / (float)heroTexH;
        ImGui::Image((ImTextureID)(intptr_t)heroTex, ImVec2(iw, ih));
        ImGui::SameLine(0, 18);
        ImGui::BeginGroup();
        ImGui::Dummy(ImVec2(0, 6));
        heroInfo();
        ImGui::EndGroup();
    } else {
        heroInfo();
    }
    ImGui::Dummy(ImVec2(0, 4));
    ImGui::EndChild();
    ImGui::Dummy(ImVec2(0, 6));

    if (m.mode == Mode::MO2 && m.vortexManagingCp) {
        ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0.32f, 0.20f, 0.04f, 0.30f));
        ImGui::BeginChild("migbanner", ImVec2(0, 0), ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_Borders);
        ImGui::PopStyleColor();
        PushBold(); ImGui::TextColored(kAmber, "Switching from Vortex?"); PopBold();
        ImGui::PushTextWrapPos(0.0f);
        ImGui::TextColored(kDim, "Vortex is set up to manage Cyberpunk on this PC. Before installing with Wabbajack: in Vortex open Mods, press Ctrl+A, Remove (untick 'Delete Archive' unless you're Nexus Premium), then Games > the Cyberpunk 3-dots > Stop Managing. The Clean step below resets the game folder + AppData.");
        ImGui::PopTextWrapPos();
        if (!m.vortexExe.empty()) {
            ImGui::Dummy(ImVec2(0, 2));
            if (ImGui::Button("Open Vortex", ImVec2(120, 0))) OpenUrl(m.vortexExe);
        }
        ImGui::EndChild();
        ImGui::Dummy(ImVec2(0, 6));
    }

    ImGui::BeginDisabled(busy);
    PushBold(); ImGui::TextColored(kHead, "Step 1   -   Set your folders"); PopBold();
    InfoHint("These two folders are for the MODLIST only. The tool's own downloads (runtimes, the Wabbajack/Vortex installer) go to a private cache automatically, so they never clutter these.");
    ImGui::Dummy(ImVec2(0, 2));
    ImGui::TextColored(kDim, "Install folder");
    InfoHint("Where Wabbajack/Vortex installs the modlist. Use an empty folder on a non-system drive, outside Program Files - e.g. D:\\Modlists\\WTNC.");
    ImGui::SetNextItemWidth(-252.0f);
    ImGui::InputText("##install", m.installPath, sizeof(m.installPath));
    ImGui::SameLine();
    if (ImGui::Button("Browse##inst", ImVec2(88, 0))) {
        std::wstring picked;
        if (BrowseForFolder(Widen(std::string(m.installPath)), picked)) {
            std::string p = cleanslate::NarrowU8(picked);
            strncpy_s(m.installPath, p.c_str(), sizeof(m.installPath) - 1);
            DetectAll(m);
            SaveState(m);
        }
    }
    ImGui::SameLine();
    if (ImGui::Button("Create folders", ImVec2(146, 0))) {
        std::error_code ec;
        std::filesystem::create_directories(std::filesystem::path(Widen(std::string(m.installPath))), ec);
        std::filesystem::create_directories(std::filesystem::path(Widen(std::string(m.downloadsPath))), ec);
    }
    ImGui::TextColored(kDim, "Downloads folder");
    InfoHint("Where the modlist's mod archives download (Wabbajack/Vortex point here). Same drive is fine. Migrating from Vortex? Point this at your old downloads to skip re-downloading.");
    ImGui::SetNextItemWidth(-252.0f);
    ImGui::InputText("##downloads", m.downloadsPath, sizeof(m.downloadsPath));
    ImGui::SameLine();
    if (ImGui::Button("Browse##dl", ImVec2(88, 0))) {
        std::wstring picked;
        if (BrowseForFolder(Widen(std::string(m.downloadsPath)), picked)) {
            std::string p = cleanslate::NarrowU8(picked);
            strncpy_s(m.downloadsPath, p.c_str(), sizeof(m.downloadsPath) - 1);
            SaveState(m);
        }
    }
    ImGui::SameLine();
    if (ImGui::Button("Open downloads", ImVec2(146, 0))) {
        std::error_code ec;
        std::filesystem::create_directories(std::filesystem::path(Widen(std::string(m.downloadsPath))), ec);
        OpenFolder(Widen(std::string(m.downloadsPath)));
    }
    ImGui::EndDisabled();
    ImGui::TextColored(kGray, "Same drive as the game, outside Program Files. The tool's own installers never land here.");

    if (m.mode == Mode::MO2 && !m.vortexDownloadsDir.empty()) {
        ImGui::Dummy(ImVec2(0, 4));
        ImGui::PushTextWrapPos(0.0f);
        ImGui::TextColored(kBlue, "Reuse your Vortex downloads: point Wabbajack's Downloads at %s (or copy the files in) to skip re-downloading.",
                           cleanslate::NarrowU8(m.vortexDownloadsDir).c_str());
        ImGui::PopTextWrapPos();
        if (ImGui::SmallButton("Open Vortex downloads")) OpenFolder(m.vortexDownloadsDir);
    }

    ImGui::Dummy(ImVec2(0, 8));
    ImGui::BeginDisabled(busy);
    if (PrimaryButton(busy ? "Working..." : "Run all automatic steps", ImVec2(-1.0f, 44.0f)))
        ImGui::OpenPopup("confirm_applyall");
    ImGui::EndDisabled();
    ImGui::TextColored(kGray, "Opens a checklist to pick from (runtimes, tweaks, %s download). Asks once for admin.",
                       (m.mode == Mode::Vortex) ? "Vortex" : "Wabbajack");
    ImGui::SameLine();
    ImGui::BeginDisabled(busy);
    if (ImGui::SmallButton("Re-scan")) DetectAll(m);
    if (ImGui::IsItemHovered()) ImGui::SetTooltip("Re-detect every check + step. Use after you install or change something by hand.");
    ImGui::SameLine();
    if (ImGui::SmallButton("Manual guide")) ImGui::OpenPopup("manual_guide");
    if (ImGui::IsItemHovered()) ImGui::SetTooltip("Every step written out with links - your fallback if anything won't run.");
    ImGui::SameLine();
    if (ImGui::SmallButton("Health report")) ImGui::OpenPopup("health_report");
    if (ImGui::IsItemHovered()) ImGui::SetTooltip("A copyable summary of every step's status - send it to the curator if you need help.");
    ImGui::EndDisabled();

    {
        ImVec2 center = ImGui::GetMainViewport()->GetCenter();
        ImGui::SetNextWindowPos(center, ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
        ImGui::SetNextWindowSize(ImVec2(700, 560), ImGuiCond_Appearing);
        bool mgOpen = true;
        if (ImGui::BeginPopupModal("Full manual guide###manual_guide", &mgOpen, 0)) {
            ImGui::TextColored(kDim, "If the tool can't do a step, do it by hand. Every step for %s, in order, with links.", m.config.name.c_str());
            ImGui::Dummy(ImVec2(0, 6));
            ImGui::BeginChild("mgbody", ImVec2(0, 440), ImGuiChildFlags_Borders);
            int gn = 0;
            for (auto& gs : m.steps) {
                if (!StepVisible(m, gs)) continue;
                ++gn;
                ImGui::PushID(gs.id.c_str());
                PushBold(); ImGui::Text("%d.  %s", gn, gs.title.c_str()); PopBold();
                ImGui::Indent(20.0f);
                if (!gs.detail.empty()) {
                    ImGui::PushTextWrapPos(0.0f);
                    ImGui::TextColored(kDim, "%s", gs.detail.c_str());
                    ImGui::PopTextWrapPos();
                }
                if (!gs.guide.empty()) {
                    ImGui::PushTextWrapPos(0.0f);
                    ImGui::TextColored(kGray, "How: %s", gs.guide.c_str());
                    ImGui::PopTextWrapPos();
                }
                if (!gs.url.empty()) {
                    if (ImGui::SmallButton("Open link")) OpenUrl(Widen(gs.url));
                }
                ImGui::Unindent(20.0f);
                ImGui::Dummy(ImVec2(0, 7));
                ImGui::PopID();
            }
            ImGui::EndChild();
            ImGui::Dummy(ImVec2(0, 6));
            if (ImGui::Button("Close", ImVec2(120, 0))) ImGui::CloseCurrentPopup();
            ImGui::EndPopup();
        }
    }

    {
        ImVec2 center = ImGui::GetMainViewport()->GetCenter();
        ImGui::SetNextWindowPos(center, ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
        ImGui::SetNextWindowSize(ImVec2(680, 540), ImGuiCond_Appearing);
        bool hrOpen = true;
        if (ImGui::BeginPopupModal("Installation health###health_report", &hrOpen, 0)) {
            ImGui::TextColored(kDim, "Status of every step. Copy it and send it to the curator if something's wrong.");
            ImGui::Dummy(ImVec2(0, 6));
            std::string report = BuildHealthReport(m);
            ImGui::BeginChild("hrbody", ImVec2(0, 410), ImGuiChildFlags_Borders, ImGuiWindowFlags_HorizontalScrollbar);
            ImGui::TextUnformatted(report.c_str());
            ImGui::EndChild();
            ImGui::Dummy(ImVec2(0, 6));
            if (ImGui::Button("Copy to clipboard", ImVec2(170, 0))) ImGui::SetClipboardText(report.c_str());
            ImGui::SameLine();
            if (ImGui::Button("Close", ImVec2(110, 0))) ImGui::CloseCurrentPopup();
            ImGui::EndPopup();
        }
    }

    {
        ImVec2 center = ImGui::GetMainViewport()->GetCenter();
        ImGui::SetNextWindowPos(center, ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
        ImGui::SetNextWindowSize(ImVec2(560, 0), ImGuiCond_Appearing);
        if (ImGui::BeginPopupModal("confirm_applyall", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
            ImGui::Text("What should run on your PC for %s?", modeName);
            ImGui::TextColored(kDim, "Pick what to apply. Nothing is deleted.");
            ImGui::Dummy(ImVec2(0, 6));
            ImGui::Checkbox("Install system components: Visual C++, .NET 8, DirectX  (administrator)", &m.optSysInstalls);
            ImGui::Checkbox("Enable Windows long paths  (administrator)", &m.optLongPaths);
            if (!(m.platform == Platform::GOG || m.platform == Platform::Epic))
                ImGui::Checkbox("Steam: update Cyberpunk only on launch  (closes Steam)", &m.optSteamTweaks);
            ImGui::Checkbox(m.mode == Mode::Vortex ? "Download + install Vortex" : "Download Wabbajack into the install folder", &m.optDownload);
            ImGui::Dummy(ImVec2(0, 6));
            ImGui::PushStyleColor(ImGuiCol_Text, kAmber);
            ImGui::Checkbox("Clean the game folder to vanilla", &m.optClean);
            ImGui::PopStyleColor();
            ImGui::TextColored(kDim, "Off by default. Moves leftover mod files to a reversible backup - nothing is deleted.");
            ImGui::Dummy(ImVec2(0, 8));
            if (ImGui::Button("Apply selected", ImVec2(140, 0))) { Launch(app, [&m]() { RunAllAuto(m); }); ImGui::CloseCurrentPopup(); }
            ImGui::SameLine();
            if (ImGui::Button("Cancel", ImVec2(120, 0))) ImGui::CloseCurrentPopup();
            ImGui::EndPopup();
        }
    }

    {
        ImVec2 center = ImGui::GetMainViewport()->GetCenter();
        ImGui::SetNextWindowPos(center, ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
        ImGui::SetNextWindowSize(ImVec2(620, 0), ImGuiCond_Appearing);
        if (ImGui::BeginPopupModal("run_summary", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
            int sumDone = 0, sumIssues = 0;
            for (auto& s : m.steps)
                if (ZoneOf(s) == Zone::Auto && StepVisible(m, s)) { if (IsResolved(s.status)) ++sumDone; else ++sumIssues; }
            if (sumIssues == 0) ImGui::TextColored(kGreen, "All set - %d action(s) complete.", sumDone);
            else ImGui::TextColored(kAmber, "%d done, %d still need your attention.", sumDone, sumIssues);
            ImGui::Dummy(ImVec2(0, 6));

            if (sumIssues > 0) {
                ImGui::BeginChild("sum_issues", ImVec2(0, 170), ImGuiChildFlags_Borders);
                for (auto& s : m.steps) {
                    if (ZoneOf(s) != Zone::Auto || !StepVisible(m, s) || IsResolved(s.status)) continue;
                    ImGui::PushID(s.id.c_str());
                    StatusDot(s.status);
                    PushBold(); ImGui::TextUnformatted(s.title.c_str()); PopBold();
                    bool adminOp = (s.id == "vcredist" || s.id == "dotnet" || s.id == "directx" || s.id == "longpaths");
                    ImGui::SameLine(ImGui::GetWindowWidth() - 88.0f);
                    ImGui::BeginDisabled(busy);
                    if (ImGui::Button("Retry", ImVec2(72, 0))) {
                        std::string sid = s.id;
                        if (adminOp) Launch(app, [&m, sid]() { RunAdminBatch(m, { sid }); });
                        else Launch(app, [&m, sid]() { ApplyStep(m, sid); });
                        ImGui::CloseCurrentPopup();
                    }
                    ImGui::EndDisabled();
                    if (!s.statusText.empty()) {
                        ImGui::Indent(22.0f);
                        ImGui::PushTextWrapPos(0.0f);
                        ImGui::TextColored(kDim, "%s", s.statusText.c_str());
                        ImGui::PopTextWrapPos();
                        ImGui::Unindent(22.0f);
                    }
                    ImGui::PopID();
                }
                ImGui::EndChild();
                ImGui::Dummy(ImVec2(0, 8));
            }

            bool hasNext = false;
            if (m.mode == Mode::MO2 && m.wabbajackReady) {
                if (PrimaryButton("Open Wabbajack", ImVec2(180, 36))) { LaunchWabbajack(m); ImGui::CloseCurrentPopup(); }
                hasNext = true;
            } else if (m.mode == Mode::Vortex && m.vortexReady && !m.config.collectionUrl.empty()) {
                if (PrimaryButton("Add to Vortex", ImVec2(180, 36))) { Launch(app, [&m]() { InstallVortexCollection(m); }); ImGui::CloseCurrentPopup(); }
                hasNext = true;
            }
            if (hasNext) ImGui::SameLine();
            if (ImGui::Button("Close", ImVec2(110, 36))) ImGui::CloseCurrentPopup();
            ImGui::EndPopup();
        }
    }

    if (busy) {
        std::string label;
        { std::lock_guard<std::mutex> lk(m.logMtx); label = m.activeLabel; }
        ImGui::TextColored(kBlue, "Working: %s", label.c_str());
        ImGui::SameLine(ImGui::GetWindowWidth() - 110.0f);
        if (m.cancelRequested.load()) ImGui::TextColored(kAmber, "cancelling...");
        else if (ImGui::Button("Cancel", ImVec2(90, 0))) m.cancelRequested = true;
        uint64_t done = m.dlDone.load();
        uint64_t total = m.dlTotal.load();
        float frac = (total > 0) ? (float)((double)done / (double)total) : -1.0f;
        static uint64_t s_lastMs = 0, s_lastBytes = 0;
        static double s_speed = 0.0;
        uint64_t nowMs = ::GetTickCount64();
        if (s_lastMs == 0 || done < s_lastBytes) { s_lastMs = nowMs; s_lastBytes = done; s_speed = 0.0; }
        else if (nowMs - s_lastMs >= 400) {
            s_speed = (double)(done - s_lastBytes) / ((double)(nowMs - s_lastMs) / 1000.0);
            s_lastMs = nowMs; s_lastBytes = done;
        }
        char overlay[160] = "";
        if (total > 0) {
            double mbDone = (double)done / 1048576.0, mbTotal = (double)total / 1048576.0;
            if (s_speed > 1.0 && done < total) {
                int eta = (int)((double)(total - done) / s_speed);
                std::snprintf(overlay, sizeof(overlay), "%.1f / %.1f MB    %.1f MB/s    ETA %d:%02d",
                              mbDone, mbTotal, s_speed / 1048576.0, eta / 60, eta % 60);
            } else {
                std::snprintf(overlay, sizeof(overlay), "%.1f / %.1f MB", mbDone, mbTotal);
            }
        }
        ImGui::ProgressBar(frac, ImVec2(-1.0f, 0.0f), (total > 0) ? overlay : "");
    }

    static bool s_logOpen = false;
    ImGui::BeginChild("body", ImVec2(0, s_logOpen ? -214.0f : -66.0f), ImGuiChildFlags_None);

    int autoDone = 0, autoTotal = 0;
    for (auto& s : m.steps)
        if (ZoneOf(s) == Zone::Auto && StepVisible(m, s)) { ++autoTotal; if (IsResolved(s.status)) ++autoDone; }
    char autoHdr[96];
    std::snprintf(autoHdr, sizeof(autoHdr), "Step 2   -   We handle these  ( %d of %d done )###auto", autoDone, autoTotal);

    ImGui::Dummy(ImVec2(0, 2));
    ImGui::SetNextItemOpen(true, ImGuiCond_Once);
    PushBold(); bool openAuto = ImGui::CollapsingHeader(autoHdr); PopBold();
    InfoHint("Runtimes, tweaks, and the mod-manager download. \"Run all\" does them in one admin prompt, or run any one with its button. Green = done.");
    if (openAuto) {
        ImGui::TextColored(kGray, "Hit \"Run all\" above, or run any single one with its button.");
        ImGui::BeginChild("autolist", ImVec2(0, 0), ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_Borders);
        bool firstAuto = true;
        for (auto& s : m.steps) {
            if (ZoneOf(s) != Zone::Auto || !StepVisible(m, s)) continue;
            if (!firstAuto) ImGui::Separator();
            firstAuto = false;
            ImGui::Dummy(ImVec2(0, 2));
            DrawAutoRow(app, s);
            ImGui::Dummy(ImVec2(0, 2));
        }
        ImGui::EndChild();

        ImGui::Dummy(ImVec2(0, 6));
        PushBold(); ImGui::TextColored(kAmber, "Clean install"); PopBold();
        DrawCleanCard(app);
    }

    int checkIssues = 0;
    for (auto& s : m.steps)
        if (ZoneOf(s) == Zone::Checks && StepVisible(m, s)) { if (!IsResolved(s.status)) ++checkIssues; }
    char checkHdr[96];
    if (checkIssues > 0) std::snprintf(checkHdr, sizeof(checkHdr), "Step 3   -   Quick checks  ( %d need attention )###chk", checkIssues);
    else std::snprintf(checkHdr, sizeof(checkHdr), "Step 3   -   Quick checks  ( all good )###chk");

    ImGui::Dummy(ImVec2(0, 8));
    ImGui::SetNextItemOpen(true, ImGuiCond_Once);
    PushBold(); bool openChk = ImGui::CollapsingHeader(checkHdr); PopBold();
    InfoHint("Read-only checks of your PC + game. Anything amber has a button that takes you where to fix it - do that before installing the list.");
    if (openChk) {
        ImGui::TextColored(kGray, "We read these from your PC. Anything amber has a button that takes you where to fix it.");
        ImGui::BeginChild("checklist_checks", ImVec2(0, 0), ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_Borders);
        bool firstCheck = true;
        for (auto& s : m.steps) {
            if (ZoneOf(s) != Zone::Checks || !StepVisible(m, s)) continue;
            if (!firstCheck) ImGui::Separator();
            firstCheck = false;
            ImGui::Dummy(ImVec2(0, 2));
            DrawCheckRow(app, s);
            ImGui::Dummy(ImVec2(0, 2));
        }
        ImGui::EndChild();
    }

    ImGui::Dummy(ImVec2(0, 8));
    ImGui::SetNextItemOpen(true, ImGuiCond_Once);
    PushBold(); bool openManual = ImGui::CollapsingHeader("Step 4   -   You do these  ( in the manager's own window )###manual"); PopBold();
    InfoHint("Steps only you can do, in the manager's own window. The \"Manual guide\" button up top lists every step with links if you get stuck.");
    if (openManual) {
        ImGui::TextColored(kGray, "We can't do these for you - but here's exactly where to go.");
        ImGui::Dummy(ImVec2(0, 4));
        int num = 0;
        for (auto& s : m.steps) {
            if (ZoneOf(s) != Zone::Manual || !StepVisible(m, s)) continue;
            DrawManualRow(app, s, ++num);
        }
    }

    ImGui::EndChild();

    ImGui::Separator();
    {
        std::string logCopy;
        { std::lock_guard<std::mutex> lk(m.logMtx); logCopy = m.log; }
        if (ImGui::Button("Copy log", ImVec2(110, 0))) ImGui::SetClipboardText(logCopy.c_str());
        ImGui::SameLine();
        if (ImGui::Button("Open log", ImVec2(110, 0))) {
            wchar_t t[1024] = {};
            DWORD n = ::GetTempPathW(1024, t);
            OpenFolder(std::wstring(t, n));
        }
        ImGui::SameLine();
        ImGui::BeginDisabled(busy || !JournalHasEntries());
        if (ImGui::Button("Undo changes", ImVec2(130, 0))) Launch(app, [&m]() { UndoAll(m); });
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::TextColored(kGray, "Each step shows its own status above. Full log at %%TEMP%%\\CyberpunkModlistSetup.log");
        s_logOpen = ImGui::CollapsingHeader("Log output");
        if (s_logOpen) {
            ImGui::BeginChild("log", ImVec2(0, 150.0f), ImGuiChildFlags_Borders, ImGuiWindowFlags_HorizontalScrollbar);
            ImGui::TextUnformatted(logCopy.c_str());
            if (busy) ImGui::SetScrollHereY(1.0f);
            ImGui::EndChild();
        }
    }
}

void DrawUI(App& app) {
    g_accent = AccentForList(app.m.list);
    auto bump = [](float v) { float x = v * 1.18f; return x > 1.0f ? 1.0f : x; };
    g_accentHover = ImVec4(bump(g_accent.x), bump(g_accent.y), bump(g_accent.z), 1.0f);
    ImGuiStyle& st = ImGui::GetStyle();
    st.Colors[ImGuiCol_CheckMark]     = g_accent;
    st.Colors[ImGuiCol_PlotHistogram] = g_accent;
    st.Colors[ImGuiCol_Header]        = ImVec4(g_accent.x, g_accent.y, g_accent.z, 0.22f);
    st.Colors[ImGuiCol_HeaderHovered] = ImVec4(g_accent.x, g_accent.y, g_accent.z, 0.38f);
    st.Colors[ImGuiCol_Separator]     = ImVec4(g_accent.x, g_accent.y, g_accent.z, 0.22f);
    st.Colors[ImGuiCol_Border]        = ImVec4(g_accent.x, g_accent.y, g_accent.z, 0.16f);

    const ImGuiViewport* vp = ImGui::GetMainViewport();
    ImGui::SetNextWindowPos(vp->WorkPos);
    ImGui::SetNextWindowSize(vp->WorkSize);
    ImGui::Begin("ModlistSetup", nullptr,
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse |
        ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoBringToFrontOnFocus);

    if (app.m.list == ListId::None) DrawListPick(app);
    else if (app.m.mode == Mode::None) DrawManagerPick(app);
    else DrawChecklist(app);

    ImGui::End();
}

}

int RunGui() {
    HINSTANCE hInstance = ::GetModuleHandleW(nullptr);
    WNDCLASSEXW wc = { sizeof(wc), CS_CLASSDC, WndProc, 0L, 0L, hInstance, nullptr, nullptr, nullptr, nullptr, L"ModlistSetup", nullptr };
    ::RegisterClassExW(&wc);

    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);

    App app;
    SuggestPaths(app.m);
    LoadState(app.m);
    if (app.m.list != ListId::None) app.m.config = ConfigForList(app.m.list);
    if (app.m.config.collectionUrl.empty() && app.m.mode == Mode::Vortex) app.m.mode = Mode::MO2;
    BuildCatalog(app.m);
    DetectAll(app.m);

    float dpiScale = (float)::GetDpiForSystem() / 96.0f;
    if (dpiScale < 1.0f) dpiScale = 1.0f;
    std::wstring title = Widen(app.m.config.name) + L" - Setup";
    HWND hwnd = ::CreateWindowW(wc.lpszClassName, title.c_str(),
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, (int)(1020 * dpiScale), (int)(780 * dpiScale), nullptr, nullptr, wc.hInstance, nullptr);

    if (!CreateDeviceD3D(hwnd)) { CleanupDeviceD3D(); ::UnregisterClassW(wc.lpszClassName, wc.hInstance); return 1; }

    ::ShowWindow(hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(hwnd);

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    g_startupDpi = dpiScale;
    ImFont* loaded = io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\segoeui.ttf", 16.0f * dpiScale);
    if (loaded) { io.FontGlobalScale = 1.0f; g_fontBase = 1.0f; }
    else { io.FontGlobalScale = kBaseFontScale * dpiScale; g_fontBase = kBaseFontScale * dpiScale; }
    g_boldFont = io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\segoeuib.ttf", 16.0f * dpiScale);
    ImGui::StyleColorsDark();
    ImGuiStyle& style = ImGui::GetStyle();
    style.WindowPadding   = ImVec2(22, 18);
    style.FramePadding    = ImVec2(11, 7);
    style.ItemSpacing     = ImVec2(8, 8);
    style.FrameRounding   = 6.0f;
    style.GrabRounding    = 6.0f;
    style.ChildRounding   = 8.0f;
    style.PopupRounding   = 8.0f;
    style.FrameBorderSize = 0.0f;
    style.WindowBorderSize = 0.0f;
    style.Colors[ImGuiCol_WindowBg]       = ImVec4(0.06f, 0.07f, 0.09f, 1.0f);
    style.Colors[ImGuiCol_ChildBg]        = ImVec4(0.10f, 0.11f, 0.14f, 1.0f);
    style.Colors[ImGuiCol_PopupBg]        = ImVec4(0.08f, 0.09f, 0.12f, 1.0f);
    style.Colors[ImGuiCol_FrameBg]        = ImVec4(0.14f, 0.16f, 0.20f, 1.0f);
    style.Colors[ImGuiCol_FrameBgHovered] = ImVec4(0.18f, 0.21f, 0.27f, 1.0f);
    style.Colors[ImGuiCol_FrameBgActive]  = ImVec4(0.20f, 0.24f, 0.31f, 1.0f);
    style.Colors[ImGuiCol_Button]         = ImVec4(0.17f, 0.20f, 0.25f, 1.0f);
    style.Colors[ImGuiCol_ButtonHovered]  = ImVec4(0.23f, 0.27f, 0.34f, 1.0f);
    style.Colors[ImGuiCol_ButtonActive]   = ImVec4(0.27f, 0.32f, 0.40f, 1.0f);
    style.Colors[ImGuiCol_Border]         = ImVec4(0.26f, 0.52f, 0.82f, 0.16f);
    style.Colors[ImGuiCol_Separator]      = ImVec4(0.26f, 0.52f, 0.82f, 0.20f);
    style.Colors[ImGuiCol_CheckMark]      = g_accent;
    style.Colors[ImGuiCol_Header]         = ImVec4(0.16f, 0.64f, 0.98f, 0.22f);
    style.Colors[ImGuiCol_HeaderHovered]  = ImVec4(0.16f, 0.64f, 0.98f, 0.36f);
    style.Colors[ImGuiCol_PlotHistogram]  = g_accent;
    style.Colors[ImGuiCol_ScrollbarGrab]  = ImVec4(0.24f, 0.28f, 0.35f, 1.0f);
    ImGui_ImplWin32_Init(hwnd);
    ImGui_ImplDX11_Init(g_pd3dDevice, g_pd3dDeviceContext);

    {
        wchar_t exe[1024] = {};
        ::GetModuleFileNameW(nullptr, exe, 1024);
        std::wstring artDir = std::filesystem::path(exe).parent_path().wstring() + L"\\art\\";
        LoadTextureFromFile(artDir + L"cab.png", &g_heroCab, &g_cabW, &g_cabH);
        LoadTextureFromFile(artDir + L"wtnc.png", &g_heroWtnc, &g_wtncW, &g_wtncH);
    }

    bool done = false;
    while (!done) {
        MSG msg;
        while (::PeekMessageW(&msg, nullptr, 0U, 0U, PM_REMOVE)) {
            ::TranslateMessage(&msg);
            ::DispatchMessageW(&msg);
            if (msg.message == WM_QUIT) done = true;
        }
        if (done) break;

        if (!app.m.busy.load() && app.m.pendingRedetect.exchange(false))
            DetectAll(app.m);

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        DrawUI(app);

        ImGui::Render();
        const float clear[4] = { 0.07f, 0.08f, 0.09f, 1.0f };
        g_pd3dDeviceContext->OMSetRenderTargets(1, &g_mainRTV, nullptr);
        g_pd3dDeviceContext->ClearRenderTargetView(g_mainRTV, clear);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
        g_pSwapChain->Present(1, 0);
    }

    if (app.worker.joinable()) app.worker.join();
    SaveState(app.m);

    if (g_heroCab) { g_heroCab->Release(); g_heroCab = nullptr; }
    if (g_heroWtnc) { g_heroWtnc->Release(); g_heroWtnc = nullptr; }
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    CleanupDeviceD3D();
    ::DestroyWindow(hwnd);
    ::UnregisterClassW(wc.lpszClassName, wc.hInstance);
    ::CoUninitialize();
    return 0;
}

int RunAdminBatchHeadless(int argc, wchar_t** argv) {
    std::wstring resultFile, installP, downloadsP;
    std::vector<std::string> ops;
    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        if (a == L"--admin-batch") continue;
        else if (a == L"--result" && i + 1 < argc) resultFile = argv[++i];
        else if (a == L"--install" && i + 1 < argc) installP = argv[++i];
        else if (a == L"--downloads" && i + 1 < argc) downloadsP = argv[++i];
        else ops.push_back(cleanslate::NarrowU8(a));
    }
    Model m;
    BuildCatalog(m);
    std::string iu = cleanslate::NarrowU8(installP);
    std::string du = cleanslate::NarrowU8(downloadsP);
    strncpy_s(m.installPath, iu.c_str(), sizeof(m.installPath) - 1);
    strncpy_s(m.downloadsPath, du.c_str(), sizeof(m.downloadsPath) - 1);

    for (const auto& op : ops) {
        if (op == "longpaths") { std::string msg; EnableLongPaths(msg); Append(m, "  " + msg); }
        else ApplyStep(m, op);
    }

    if (!resultFile.empty()) {
        std::ofstream f(std::filesystem::path(resultFile), std::ios::binary | std::ios::trunc);
        if (f) { std::lock_guard<std::mutex> lk(m.logMtx); f << m.log; }
    }
    return 0;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    bool admin = false;
    for (int i = 1; i < argc; ++i) if (std::wstring(argv[i]) == L"--admin-batch") { admin = true; break; }
    int rc = admin ? RunAdminBatchHeadless(argc, argv) : RunGui();
    if (argv) ::LocalFree(argv);
    return rc;
}
